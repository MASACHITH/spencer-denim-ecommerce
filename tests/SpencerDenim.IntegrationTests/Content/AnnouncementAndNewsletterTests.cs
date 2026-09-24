using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SpencerDenim.Domain.Marketing;
using SpencerDenim.Infrastructure.Persistence;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Content;

/// <summary>
/// The announcement strip (GLB-03) and the newsletter signup (HOME-10) against the real API.
/// </summary>
public sealed class AnnouncementAndNewsletterTests(SqlServerDatabaseFixture sqlServer) : ContentTestBase(sqlServer)
{
    [Fact]
    public async Task The_seeded_announcements_are_served_in_order()
    {
        var messages = await CreateApi().GetAnnouncementsAsync(Token);

        Assert.Equal(2, messages.Count);
        Assert.All(messages, message => Assert.False(string.IsNullOrWhiteSpace(message.Message)));
    }

    [Fact]
    public async Task A_deactivated_message_leaves_the_strip()
    {
        var api = CreateApi();
        var first = (await api.GetAnnouncementsAsync(Token))[0];

        await SetActiveAsync(first.Id, false);
        try
        {
            var messages = await api.GetAnnouncementsAsync(Token);

            Assert.Single(messages);
            Assert.DoesNotContain(messages, message => message.Id == first.Id);
        }
        finally
        {
            await SetActiveAsync(first.Id, true);
        }

        Assert.Equal(2, (await api.GetAnnouncementsAsync(Token)).Count);
    }

    /// <summary>
    /// Lesson L1. The strip is on every page of the site, so a line the client has emptied by
    /// accident must not become a blank band across all of them.
    /// </summary>
    [Fact]
    public async Task A_message_of_nothing_but_spaces_is_dropped_rather_than_rendered_blank()
    {
        var api = CreateApi();
        var first = (await api.GetAnnouncementsAsync(Token))[0];
        var original = first.Message;

        await SetMessageAsync(first.Id, "   ");
        try
        {
            Assert.DoesNotContain(await api.GetAnnouncementsAsync(Token), message => message.Id == first.Id);
        }
        finally
        {
            await SetMessageAsync(first.Id, original);
        }
    }

    [Fact]
    public async Task The_announcements_endpoint_answers_head_as_well_as_get()
    {
        using var response = await CreateApi().HeadAsync("/api/content/announcements", Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_signup_writes_a_consent_row()
    {
        var api = CreateApi();
        const string Email = "new-subscriber@example.com";

        await RemoveAsync(Email);
        try
        {
            var (status, body) = await api.SubscribeAsync(new { email = Email }, Token);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));

            var row = await FindAsync(Email);

            Assert.NotNull(row);
            Assert.Equal(NewsletterSubscriberStatus.Subscribed, row.Status);
            Assert.Equal(NewsletterSource.Homepage, row.Source);
            Assert.NotEqual(Guid.Empty, row.UnsubscribeToken);
            Assert.NotEqual(default, row.ConsentAtUtc);
            Assert.Null(row.UnsubscribedAtUtc);
        }
        finally
        {
            await RemoveAsync(Email);
        }
    }

    /// <summary>
    /// A repeat submission is the same answer and one row. Saying "you are already subscribed"
    /// would turn the form into a way of testing whether an address shops here.
    /// </summary>
    [Fact]
    public async Task A_second_signup_says_the_same_thing_and_leaves_one_row()
    {
        var api = CreateApi();
        const string Email = "repeat@example.com";

        await RemoveAsync(Email);
        try
        {
            var (firstStatus, firstBody) = await api.SubscribeAsync(new { email = Email }, Token);
            var (secondStatus, secondBody) = await api.SubscribeAsync(
                new { email = "REPEAT@example.com" },
                Token);

            Assert.Equal(HttpStatusCode.OK, firstStatus);
            Assert.Equal(HttpStatusCode.OK, secondStatus);
            Assert.Equal(
                firstBody.GetProperty("message").GetString(),
                secondBody.GetProperty("message").GetString());

            Assert.Equal(1, await CountAsync(Email));
        }
        finally
        {
            await RemoveAsync(Email);
        }
    }

    /// <summary>
    /// The consent time records when the address actually opted in. A repeat submission must not
    /// overwrite it, or the one field with evidential value is destroyed by a double click.
    /// </summary>
    [Fact]
    public async Task A_repeat_signup_does_not_overwrite_the_original_consent()
    {
        var api = CreateApi();
        const string Email = "consent@example.com";

        await RemoveAsync(Email);
        try
        {
            await api.SubscribeAsync(new { email = Email }, Token);
            var first = (await FindAsync(Email))!.ConsentAtUtc;

            await api.SubscribeAsync(new { email = Email }, Token);

            Assert.Equal(first, (await FindAsync(Email))!.ConsentAtUtc);
        }
        finally
        {
            await RemoveAsync(Email);
        }
    }

    [Fact]
    public async Task An_unsubscribed_address_can_consent_again()
    {
        var api = CreateApi();
        const string Email = "returning@example.com";

        await RemoveAsync(Email);
        try
        {
            await api.SubscribeAsync(new { email = Email }, Token);
            await UnsubscribeAsync(Email);

            var (status, _) = await api.SubscribeAsync(new { email = Email }, Token);
            var row = await FindAsync(Email);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.NotNull(row);
            Assert.Equal(NewsletterSubscriberStatus.Subscribed, row.Status);
            Assert.Null(row.UnsubscribedAtUtc);
            Assert.Equal(1, await CountAsync(Email));
        }
        finally
        {
            await RemoveAsync(Email);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-address")]
    [InlineData("shopper@localhost")]
    public async Task An_address_the_store_will_not_hold_is_a_400_naming_the_field(string email)
    {
        var (status, body) = await CreateApi().SubscribeAsync(new { email }, Token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.True(body.GetProperty("errors").TryGetProperty("email", out var messages));
        Assert.NotEmpty(messages.EnumerateArray().ToList());

        Assert.Equal(0, await CountAsync(email));
    }

    /// <summary>
    /// GLB-07. The footer form is on every page, and doc 08 §3.6's <c>Source</c> is what tells the
    /// client in ADM-21 where an address came from.
    /// </summary>
    [Fact]
    public async Task A_footer_signup_is_recorded_as_coming_from_the_footer()
    {
        var api = CreateApi();
        const string Email = "footer-signup@example.com";

        await RemoveAsync(Email);
        try
        {
            var (status, _) = await api.SubscribeAsync(new { email = Email, source = "footer" }, Token);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(NewsletterSource.Footer, (await FindAsync(Email))!.Source);
        }
        finally
        {
            await RemoveAsync(Email);
        }
    }

    /// <summary>
    /// <c>Source</c> is a <c>varchar(30)</c> the client reads in a report, on a public endpoint.
    /// Anything the API does not recognise is the homepage — the caller's string never reaches the
    /// column.
    /// </summary>
    [Theory]
    [InlineData("instagram-campaign")]
    [InlineData("'; DROP TABLE marketing.NewsletterSubscribers; --")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_source_the_api_does_not_know_is_recorded_as_the_homepage(string? source)
    {
        var api = CreateApi();
        const string Email = "unknown-source@example.com";

        await RemoveAsync(Email);
        try
        {
            var (status, _) = await api.SubscribeAsync(new { email = Email, source }, Token);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal(NewsletterSource.Homepage, (await FindAsync(Email))!.Source);
        }
        finally
        {
            await RemoveAsync(Email);
        }
    }

    [Fact]
    public async Task A_body_with_no_email_at_all_is_a_400_rather_than_a_500()
    {
        var (status, _) = await CreateApi().SubscribeAsync(new { }, Token);

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    private async Task<NewsletterSubscriber?> FindAsync(string email)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = email.ToUpperInvariant();

        return await dbContext.NewsletterSubscribers
            .AsNoTracking()
            .FirstOrDefaultAsync(subscriber => subscriber.EmailNormalized == key, Token);
    }

    private async Task<int> CountAsync(string email)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = email.ToUpperInvariant();

        return await dbContext.NewsletterSubscribers
            .CountAsync(subscriber => subscriber.EmailNormalized == key, Token);
    }

    private async Task RemoveAsync(string email)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = email.ToUpperInvariant();

        await dbContext.NewsletterSubscribers
            .Where(subscriber => subscriber.EmailNormalized == key)
            .ExecuteDeleteAsync(Token);
    }

    private async Task UnsubscribeAsync(string email)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var key = email.ToUpperInvariant();

        await dbContext.NewsletterSubscribers
            .Where(subscriber => subscriber.EmailNormalized == key)
            .ExecuteUpdateAsync(
                update => update
                    .SetProperty(subscriber => subscriber.Status, NewsletterSubscriberStatus.Unsubscribed)
                    .SetProperty(subscriber => subscriber.UnsubscribedAtUtc, DateTime.UtcNow),
                Token);
    }

    private async Task SetActiveAsync(int id, bool isActive)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.AnnouncementMessages
            .Where(message => message.Id == id)
            .ExecuteUpdateAsync(update => update.SetProperty(message => message.IsActive, isActive), Token);
    }

    private async Task SetMessageAsync(int id, string message)
    {
        using var scope = SqlServer.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await dbContext.AnnouncementMessages
            .Where(candidate => candidate.Id == id)
            .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.Message, message), Token);
    }
}
