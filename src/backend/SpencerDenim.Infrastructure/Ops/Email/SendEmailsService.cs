using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SpencerDenim.Application.Abstractions.Time;
using SpencerDenim.Application.Ops.Email;
using SpencerDenim.Application.Ops.Email.Abstractions;
using SpencerDenim.Domain.Ops;

namespace SpencerDenim.Infrastructure.Ops.Email;

/// <summary>
/// <c>SendEmails</c> (doc 11 §1.4's job table, §4, doc 08 §3.8): hands the queued messages to the mail
/// server, with the backoff ladder of <see cref="EmailBackoff"/>.
/// <para>
/// <b>It must never take the host down and it must never go quiet.</b> Both halves are
/// <c>ExpireUnpaidOrders</c>'s lessons, taken from <c>CatalogSeedHostedService</c> before it, and this
/// job follows them line for line: an exception out of <see cref="ExecuteAsync"/> faults the host, so
/// every cycle is wrapped; and a repeated failure drops to <see cref="LogLevel.Warning"/> after the first
/// and resets on the first success, because a job that logs a stack trace every thirty seconds for a day
/// is a job whose log nobody reads.
/// </para>
/// <para>
/// <b>No lease and no leader election.</b> <c>ops.JobLeases</c> is specified (doc 08 §3.8) and does not
/// exist, and this job does not need one: <see cref="IEmailOutboxStore.ClaimAsync"/>'s conditional
/// <c>UPDATE</c> means that of any number of senders racing for one message exactly one gets it. A lease
/// would make the job run once; the claim makes each <i>message</i> send once, which is the invariant
/// that matters.
/// </para>
/// <para>
/// <b>The behaviour that matters most today is what it does with no SMTP configured, because that is the
/// state the shop is actually in.</b> We do not have the client's mail credentials. So before the sweep
/// claims anything it asks the transport whether it could send at all, and if it could not it <b>leaves
/// the queue exactly as it found it</b> — nothing Sending, nothing Failed, nothing Dead and above all
/// nothing Sent — and says so with a count, at <see cref="LogLevel.Warning"/>, at most once every
/// <c>Email:UnconfiguredWarningMinutes</c>. An operator can therefore tell the two quiet states apart:
/// <b>silence means there is nothing to send</b>, and a repeating warning naming a growing number means
/// there is plenty to send and no way to send it. Treating an unconfigured host as a failing one would
/// burn the whole six-rung ladder against a host that does not exist and kill every order email the shop
/// had queued, five hours after it was deployed.
/// </para>
/// </summary>
internal sealed partial class SendEmailsService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    EmailOptions options,
    ILogger<SendEmailsService> logger) : BackgroundService
{
    /// <summary>How many consecutive sweeps have failed, so the first one is loud and the rest are not.</summary>
    private int _consecutiveFailures;

    /// <summary>
    /// When the "SMTP is not configured" warning was last written, so it repeats on a schedule rather
    /// than per sweep. <see cref="DateTime.MinValue"/> until the first one.
    /// </summary>
    private DateTime _unconfiguredWarnedAtUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first sweep waits one interval rather than running at startup, for ExpireUnpaidOrders'
        // reason: starting work before the host is listening is what made the seeder's readiness signal
        // unreachable for a minute (W3-02 round 4). Thirty seconds is also nothing to a message that has
        // been sitting in the queue since the last restart.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.SweepSeconds));

        while (await WaitAsync(timer, stoppingToken))
        {
            await SweepAsync(stoppingToken);
        }
    }

    /// <summary>Waits for the next tick, answering <see langword="false"/> when the host is stopping.</summary>
    /// <remarks>
    /// <see cref="OperationCanceledException"/> is swallowed rather than allowed out: it is the ordinary
    /// way a <see cref="PeriodicTimer"/> ends on shutdown, and letting it propagate logs a cancelled task
    /// out of a perfectly healthy container stop.
    /// </remarks>
    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task SweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IEmailOutboxStore>();
            var transport = scope.ServiceProvider.GetRequiredService<IEmailTransport>();

            var now = clock.UtcNow.UtcDateTime;

            if (!transport.IsConfigured)
            {
                await ReportUnsendableAsync(store, now, stoppingToken);
                _consecutiveFailures = 0;
                return;
            }

            var released = await store.ReleaseExpiredLeasesAsync(now, stoppingToken);

            if (released > 0)
            {
                LogLeasesReleased(logger, released);
            }

            var claims = await store.ClaimAsync(
                options.BatchSize,
                now,
                now.AddMinutes(options.LeaseMinutes),
                stoppingToken);

            if (claims.Count == 0)
            {
                // Nothing to send. Deliberately silent: this is the normal state of a shop between
                // orders, and a line here would bury the ones that matter.
                _consecutiveFailures = 0;
                return;
            }

            var sent = 0;
            var failed = 0;
            var dead = 0;

            foreach (var claim in claims)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    // The remaining claims keep their lease and are released by a later sweep, which is
                    // why ReleaseExpiredLeasesAsync exists. Nothing is lost by stopping here.
                    break;
                }

                // The clock is re-read per message, not reused from the batch: a batch of twenty against
                // a slow mail server can take long enough that the backoff of the last one would be
                // measured from a stale instant, and NextAttemptAtUtc has to be in the real future.
                var outcome = await SendOneAsync(store, transport, claim, stoppingToken);

                switch (outcome)
                {
                    case SendOutcome.Sent:
                        sent++;
                        break;
                    case SendOutcome.Failed:
                        failed++;
                        break;
                    case SendOutcome.Dead:
                        dead++;
                        break;
                }
            }

            _consecutiveFailures = 0;
            LogSwept(logger, sent, failed, dead);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down between the claim and the first send. Nothing to say.
        }
        catch (Exception exception)
        {
            // The sweep itself could not run — the database is unreachable, or the claim threw. The host
            // stays up and the next tick tries again; what must not happen is a stack trace every thirty
            // seconds for a day, which is how a signal stops being one.
            var failures = Interlocked.Increment(ref _consecutiveFailures);

            if (failures == 1)
            {
                LogSweepFailed(logger, exception);
            }
            else
            {
                LogSweepStillFailing(logger, failures);
            }
        }
    }

    /// <summary>
    /// One message: hand it to the transport, then record what happened on its row.
    /// </summary>
    /// <remarks>
    /// <b>A failure here is one message's failure and never the sweep's.</b> A refused recipient is a
    /// property of that address — doc 11 §4 is explicit that a wrong address on a placed order cannot be
    /// corrected — and letting it stop the loop would mean one bad address held up every other
    /// customer's bank details. So each message is caught on its own and the batch carries on.
    /// </remarks>
    private async Task<SendOutcome> SendOneAsync(
        IEmailOutboxStore store,
        IEmailTransport transport,
        EmailOutboxClaim claim,
        CancellationToken stoppingToken)
    {
        try
        {
            await transport.SendAsync(
                new OutboundEmail(claim.ToEmail, claim.ToName, claim.Subject, claim.BodyHtml, claim.BodyText),
                stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping mid-send. The lease stands and a later sweep releases it; recording a
            // failure here would spend a rung of the ladder on a container restart.
            return SendOutcome.Cancelled;
        }
        catch (Exception exception)
        {
            // **The exception's own message, and not the body, the subject or the recipient.** Doc 08
            // §3.8 keeps this table out of the audit log precisely because its contents are sensitive,
            // and a LastError holding a rendered email would put the thing we are careful about into the
            // column we are not.
            var isDead = await store.MarkFailedAsync(
                claim,
                $"{exception.GetType().Name}: {exception.Message}",
                clock.UtcNow.UtcDateTime,
                stoppingToken);

            if (isDead)
            {
                LogMessageDead(logger, claim.TemplateCode, claim.RelatedEntityId, claim.Id, exception);
                return SendOutcome.Dead;
            }

            LogMessageFailed(logger, claim.Id, claim.TemplateCode, claim.AttemptCount, claim.MaxAttempts);
            return SendOutcome.Failed;
        }

        await store.MarkSentAsync(claim.Id, claim.TemplateCode, clock.UtcNow.UtcDateTime, stoppingToken);

        return SendOutcome.Sent;
    }

    /// <summary>
    /// Says, at most every <c>Email:UnconfiguredWarningMinutes</c>, how many messages are waiting for an
    /// SMTP server that is not configured — and says nothing at all when none is.
    /// </summary>
    /// <remarks>
    /// <b>The count is read before the warning is written, not after the throttle.</b> The point of the
    /// line is the number: "nothing to send" and "cannot send" are the two states an operator has to be
    /// able to tell apart, and a throttled warning with no count would collapse them back together the
    /// moment the queue emptied.
    /// </remarks>
    private async Task ReportUnsendableAsync(
        IEmailOutboxStore store,
        DateTime nowUtc,
        CancellationToken stoppingToken)
    {
        var due = await store.CountDueAsync(nowUtc, stoppingToken);

        if (due == 0)
        {
            return;
        }

        if (_unconfiguredWarnedAtUtc != DateTime.MinValue
            && nowUtc - _unconfiguredWarnedAtUtc < TimeSpan.FromMinutes(options.UnconfiguredWarningMinutes))
        {
            return;
        }

        _unconfiguredWarnedAtUtc = nowUtc;
        LogNotConfigured(logger, due);
    }

    private enum SendOutcome
    {
        Sent,
        Failed,
        Dead,
        Cancelled,
    }

    [LoggerMessage(
        EventId = 3401,
        Level = LogLevel.Information,
        Message = "Sent {Sent} email(s); {Failed} will be retried and {Dead} have run out of attempts.")]
    private static partial void LogSwept(ILogger logger, int sent, int failed, int dead);

    [LoggerMessage(
        EventId = 3402,
        Level = LogLevel.Warning,
        Message = "{Due} email(s) are queued and cannot be sent, because this host has no SMTP "
            + "configuration. Nothing has been lost: the messages are untouched and will go out as soon "
            + "as Email__Smtp__Host and Email__Smtp__FromAddress are set (doc 08 §3.8's Email.Smtp "
            + "group). This is repeated periodically for as long as the queue is not empty.")]
    private static partial void LogNotConfigured(ILogger logger, int due);

    [LoggerMessage(
        EventId = 3403,
        Level = LogLevel.Warning,
        Message = "Email {MessageId} ({TemplateCode}) failed on attempt {AttemptCount} of "
            + "{MaxAttempts} and will be retried. The reason is on the row's LastError.")]
    private static partial void LogMessageFailed(
        ILogger logger,
        long messageId,
        string templateCode,
        short attemptCount,
        short maxAttempts);

    [LoggerMessage(
        EventId = 3404,
        Level = LogLevel.Error,
        Message = "Email {TemplateCode} for order {OrderPublicId} (outbox row {MessageId}) has failed "
            + "every attempt and will not be retried, so that customer has not been told. Doc 11 §4 "
            + "(G-37) asks for an EmailDeliveryFailed dashboard alert here; ops.AdminAlerts does not "
            + "exist in this build, so this log line is the whole signal. Open the order and contact the "
            + "customer by telephone (doc 11 §6.1 step 6).")]
    private static partial void LogMessageDead(
        ILogger logger,
        string templateCode,
        string? orderPublicId,
        long messageId,
        Exception exception);

    [LoggerMessage(
        EventId = 3405,
        Level = LogLevel.Warning,
        Message = "Put {Released} email(s) back in the queue whose sender stopped before it finished. "
            + "This is normal after a restart during a send; if it repeats without one, a sender is "
            + "losing its database connection mid-batch.")]
    private static partial void LogLeasesReleased(ILogger logger, int released);

    [LoggerMessage(
        EventId = 3406,
        Level = LogLevel.Error,
        Message = "The email sweep could not run, so nothing is being sent to customers. The next "
            + "attempt is one sweep away.")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3407,
        Level = LogLevel.Warning,
        Message = "The email sweep has now failed {Failures} times in a row. The first failure was "
            + "logged with its exception.")]
    private static partial void LogSweepStillFailing(ILogger logger, int failures);
}
