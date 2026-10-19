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
/// <para>
/// <b>Every message that reaches <c>Dead</c> raises an <c>EmailDeliveryFailed</c> alert</b> (doc 11 §4,
/// §7 G-37), which is what turns "nobody was told" into a banner a Super Admin has to acknowledge. There
/// are two ways a row gets there and both go through <see cref="IDeadEmailAlerts"/>: the last failed
/// attempt, and a sender that died holding its lease on a message's <i>final</i> attempt — the second of
/// which is invisible from the failure handler, which is why
/// <see cref="IEmailOutboxStore.KillStrandedExhaustedAsync"/> reports the ids it killed instead of only
/// how many.
/// </para>
/// <para>
/// <b>The <c>Error</c> line is still written beside the alert, and is not redundant.</b> The alert is a
/// separate statement from the one that killed the row (see <see cref="IDeadEmailAlerts"/>), so a
/// database that becomes unreachable between them leaves the log line as the whole signal — which is
/// exactly the behaviour of the build before this one, rather than silence. A log line nobody reads is
/// also the thing <c>ops.AdminAlerts</c> exists to replace, so the alert is the signal and the line is
/// the fallback.
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

    /// <summary>
    /// One pass. <b><c>internal</c> rather than <c>private</c> so the unit suite can run a sweep
    /// directly</b>, against a fake <see cref="IEmailTransport"/> and a fake
    /// <see cref="IEmailOutboxStore"/> — which is the only way to prove the ladder, the dead status and
    /// the unconfigured-SMTP behaviour without a mail server and without waiting for a timer. It is not a
    /// test-only member: it is the whole job, and <see cref="ExecuteAsync"/> is the loop around it.
    /// </summary>
    internal async Task SweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IEmailOutboxStore>();
            var transport = scope.ServiceProvider.GetRequiredService<IEmailTransport>();
            var alerts = scope.ServiceProvider.GetRequiredService<IDeadEmailAlerts>();

            var now = clock.UtcNow.UtcDateTime;

            if (!transport.IsConfigured)
            {
                await ReportUnsendableAsync(store, now, stoppingToken);
                _consecutiveFailures = 0;
                return;
            }

            // **Before the release, because the release no longer kills anything.** A stranded row on
            // its final attempt is terminal, and doc 11 §4 (G-37) owes it the same alert as a row that
            // ran out of ladder in front of a live sender.
            //
            // **The order reads as the guard and is not one.** What keeps an exhausted row off the
            // ladder is the requeue's own `AND AttemptCount < MaxAttempts`, which is why
            // `The_requeue_alone_does_not_put_an_exhausted_row_back_on_the_ladder` calls the requeue by
            // itself: reversing these two lines changes nothing. Kill first is still the right reading
            // order for anyone following the sweep, and nothing depends on it — said plainly here
            // because a comment claiming the ordering is load-bearing invites deleting the clause that
            // actually is.
            foreach (var killed in await store.KillStrandedExhaustedAsync(now, stoppingToken))
            {
                LogMessageStranded(logger, killed);
                await RaiseDeadAlertAsync(alerts, killed, stoppingToken);
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

                // `SendOneAsync` re-reads the clock for each message rather than taking `now` from this
                // sweep: a batch of twenty against a slow mail server can take long enough that the
                // backoff of the last one would be measured from a stale instant, and
                // `NextAttemptAtUtc` has to be in the real future.
                var outcome = await SendOneAsync(store, transport, alerts, claim, stoppingToken);

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

                    case SendOutcome.Cancelled:
                        // Counted in none of the three, deliberately: the host is stopping, the message
                        // still holds its lease, and a later sweep releases it. Reporting it as a failure
                        // would put a number in the log for something that did not happen.
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
        IDeadEmailAlerts alerts,
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
            var outcome = await store.MarkFailedAsync(
                claim,
                $"{exception.GetType().Name}: {exception.Message}",
                clock.UtcNow.UtcDateTime,
                stoppingToken);

            switch (outcome)
            {
                case EmailFailureOutcome.Dead:
                    // The row id, never claim.RelatedEntityId: for an order email that is the order's public
                    // token, the order page's only credential (issue #201), and this line is written at Error
                    // to a log Docker keeps. The banner raised just below carries the order for the people
                    // who are allowed to see it.
                    LogMessageDead(logger, claim.TemplateCode, claim.Id, exception);
                    await RaiseDeadAlertAsync(alerts, claim.Id, stoppingToken);
                    return SendOutcome.Dead;

                case EmailFailureOutcome.NotOurs:
                    LogLeaseLost(logger, claim.Id, claim.AttemptCount);
                    return SendOutcome.Failed;

                default:
                    LogMessageFailed(
                        logger, claim.Id, claim.TemplateCode, claim.AttemptCount, claim.MaxAttempts);
                    return SendOutcome.Failed;
            }
        }

        if (!await store.MarkSentAsync(claim, clock.UtcNow.UtcDateTime, stoppingToken))
        {
            // The message went out, but the row had already been reclaimed by another sender — so that
            // sender will send it again and the customer gets two. This is the only way a duplicate can
            // happen, it is logged with the row id so it can be matched to the other sender's line, and
            // the fix if it ever repeats is a longer `Email:LeaseMinutes`, not a change here.
            LogLeaseLost(logger, claim.Id, claim.AttemptCount);
        }

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

    /// <summary>
    /// Raises the <c>EmailDeliveryFailed</c> alert for one dead row, and never lets a failure to raise it
    /// stop the sweep.
    /// </summary>
    /// <remarks>
    /// <b>Caught here and not left to the sweep's own handler, for the reason one message's failure is
    /// caught in <see cref="SendOneAsync"/>:</b> nineteen other customers' bank details are in the same
    /// batch, and an alert that could not be written must not be the reason they wait another thirty
    /// seconds. What is lost is the banner; what is kept is the <c>Error</c> line beside it, which named
    /// the same three facts before this alert existed.
    /// </remarks>
    private async Task RaiseDeadAlertAsync(
        IDeadEmailAlerts alerts,
        long messageId,
        CancellationToken stoppingToken)
    {
        try
        {
            await alerts.RaiseAsync(messageId, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping. The row is Dead and has no alert; the Error line above it stands, and
            // the next sweep does not come back for it — which is said plainly here rather than left for
            // a reader to infer, because it is the one case where the banner is missing and the row is
            // not.
            LogDeadAlertNotRaised(logger, messageId);
        }
        catch (Exception exception)
        {
            LogDeadAlertFailed(logger, messageId, exception);
        }
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
        Message = "Email {TemplateCode} (outbox row {MessageId}) has failed "
            + "every attempt and will not be retried, so that customer has not been told. An "
            + "EmailDeliveryFailed alert is raised for it (doc 11 §4, G-37) and shows as a banner to "
            + "every Super Admin until one of them acknowledges it; the banner names the order. Open the "
            + "order and contact the customer by telephone or WhatsApp (doc 11 §6.1 step 6): there is no "
            + "resend action in this build (doc 11 §7 G-42).")]
    private static partial void LogMessageDead(
        ILogger logger,
        string templateCode,
        long messageId,
        Exception exception);

    [LoggerMessage(
        EventId = 3409,
        Level = LogLevel.Error,
        Message = "Outbox row {MessageId} was claimed on its last attempt by a sender that never "
            + "finished, so the message will not be sent and that customer has not been told. An "
            + "EmailDeliveryFailed alert is raised for it (doc 11 §4, G-37). If this repeats without a "
            + "restart, a sender is losing its database connection mid-batch.")]
    private static partial void LogMessageStranded(ILogger logger, long messageId);

    [LoggerMessage(
        EventId = 3410,
        Level = LogLevel.Error,
        Message = "Outbox row {MessageId} is Dead and its EmailDeliveryFailed alert could not be "
            + "written, so no banner will appear for it. The Error line above names the message; act on "
            + "that one, because nothing will raise this alert later.")]
    private static partial void LogDeadAlertFailed(ILogger logger, long messageId, Exception exception);

    [LoggerMessage(
        EventId = 3411,
        Level = LogLevel.Warning,
        Message = "Outbox row {MessageId} is Dead and its EmailDeliveryFailed alert was not written "
            + "because the host is shutting down. No banner will appear for it; the Error line above "
            + "names the message.")]
    private static partial void LogDeadAlertNotRaised(ILogger logger, long messageId);

    [LoggerMessage(
        EventId = 3408,
        Level = LogLevel.Warning,
        Message = "Email {MessageId} was handled on attempt {AttemptCount}, but its row had already been "
            + "reclaimed by another sender, so the result could not be recorded against it. The message "
            + "may reach the customer twice. Lengthen Email__LeaseMinutes if this repeats.")]
    private static partial void LogLeaseLost(ILogger logger, long messageId, short attemptCount);

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
