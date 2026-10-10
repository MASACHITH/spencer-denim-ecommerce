using System.Net;
using System.Text;
using SpencerDenim.Application.Abstractions.Messaging;
using SpencerDenim.Application.Sales.Payments.PayHere;
using SpencerDenim.IntegrationTests.Infrastructure;

namespace SpencerDenim.IntegrationTests.Sales;

/// <summary>
/// The notify endpoint's request-body ceiling, on a <b>real Kestrel</b> (doc 11 P8b step 4).
/// <para>
/// The endpoint lowers <c>MaxRequestBodySize</c> to 16,384 bytes before it reads the form, so a request
/// nobody but an attacker would send is refused without being buffered. <see cref="ApiFactory"/>'s
/// <c>TestServer</c> has no transport and ignores every size limit, so a test against it passes whatever the
/// endpoint does - which is how the cap shipped with no test at all. <see cref="KestrelApiHost"/> is the host
/// that can fail on this, and it needs no database: the handler behind the endpoint is a stub that counts the
/// requests that reached it.
/// </para>
/// <para>
/// <b>Plain and chunked are different code paths in Kestrel</b>: a request with a <c>Content-Length</c> over
/// the limit is refused as soon as the body is first read, and a chunked one only when the running count
/// crosses it. Both are covered, at the boundary on each side.
/// </para>
/// </summary>
public sealed class PayHereTransportTests : IAsyncDisposable
{
    /// <summary>The ceiling <c>PayHereEndpoints</c> sets. Written out here so a change to it is a change to this test.</summary>
    private const int Limit = 16 * 1024;

    private readonly CountingHandler _handler = new();
    private KestrelApiHost? _host;
    private HttpClient? _client;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_normal_sized_notification_reaches_the_handler()
    {
        using var response = await PostAsync(FormOfExactly(400), chunked: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, _handler.Calls);
    }

    /// <summary>The boundary for a request that states its length: exactly the limit is accepted.</summary>
    [Fact]
    public async Task A_plain_body_of_exactly_the_limit_is_accepted()
    {
        var body = FormOfExactly(Limit);
        Assert.Equal(Limit, body.Length);

        using var response = await PostAsync(body, chunked: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, _handler.Calls);

        // And what arrived is the form, intact: the padding field is the only thing the endpoint ignores.
        Assert.Equal("1211149", _handler.LastFields!["merchant_id"]);
        Assert.Equal("SD-2026-000123-4567", _handler.LastFields["order_id"]);
    }

    [Fact]
    public async Task A_plain_body_one_byte_over_the_limit_is_refused_with_413_before_the_handler_sees_it()
    {
        var body = FormOfExactly(Limit + 1);
        Assert.Equal(Limit + 1, body.Length);

        using var response = await PostAsync(body, chunked: false);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, _handler.Calls);
    }

    /// <summary>
    /// <b>For a chunked request Kestrel counts the bytes on the wire, chunk framing included</b> - measured, not
    /// assumed: with 2 KB chunks a payload of 16,323 bytes (16,384 less 61 bytes of framing) is accepted and one
    /// of 16,324 is refused. So a chunked payload of exactly 16,384 bytes is refused; that is the ceiling doing
    /// its job on the thing an attacker controls, and PayHere's notify carries a <c>Content-Length</c> in any
    /// case. The assertion is therefore stated in wire bytes, with the framing computed rather than hard-coded.
    /// </summary>
    [Fact]
    public async Task A_chunked_body_is_accepted_up_to_the_limit_counting_its_framing_and_refused_beyond_it()
    {
        var largestThatFits = Enumerable.Range(Limit - 512, 513).Reverse().First(size => ChunkedWireSize(size) <= Limit);

        // Comfortably inside, and the largest that fits exactly.
        foreach (var size in new[] { 400, Limit - 512, largestThatFits })
        {
            using var response = await PostAsync(FormOfExactly(size), chunked: true);

            Assert.True(
                response.StatusCode == HttpStatusCode.OK,
                $"A chunked payload of {size} bytes ({ChunkedWireSize(size)} on the wire) was answered {(int)response.StatusCode}.");
        }

        var accepted = _handler.Calls;
        Assert.Equal(3, accepted);

        // One byte more on the wire, the payload that is exactly the limit, and one over it.
        foreach (var size in new[] { largestThatFits + 1, Limit, Limit + 1 })
        {
            using var response = await PostAsync(FormOfExactly(size), chunked: true);

            Assert.True(
                response.StatusCode == HttpStatusCode.RequestEntityTooLarge,
                $"A chunked payload of {size} bytes ({ChunkedWireSize(size)} on the wire) was answered {(int)response.StatusCode}.");
        }

        // Refused before the handler ever saw them.
        Assert.Equal(accepted, _handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_body_far_over_the_limit_is_refused_too(bool chunked)
    {
        using var response = await PostAsync(FormOfExactly(Limit * 8), chunked);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(0, _handler.Calls);
    }

    [Fact]
    public async Task A_refusal_names_nothing_of_the_request()
    {
        using var response = await PostAsync(FormOfExactly(Limit + 1), chunked: false);

        var text = await response.Content.ReadAsStringAsync(Token);

        Assert.DoesNotContain("1211149", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SD-2026-000123", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_limit_does_not_outlive_the_request()
    {
        // A refused request must not leave the next one refused: the ceiling is per request.
        using var refused = await PostAsync(FormOfExactly(Limit + 1), chunked: false);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, refused.StatusCode);

        using var accepted = await PostAsync(FormOfExactly(Limit), chunked: false);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, _handler.Calls);
    }

    // ---- helpers -----------------------------------------------------------------------------------

    /// <summary>
    /// A form-urlencoded body of <b>exactly</b> <paramref name="bytes"/> bytes: the signed fields a
    /// notification carries, padded with one more field (which the endpoint ignores). All ASCII, so characters
    /// and bytes are the same number.
    /// </summary>
    private static string FormOfExactly(int bytes)
    {
        const string Head =
            "merchant_id=1211149&order_id=SD-2026-000123-4567&payment_id=320032123456&payhere_amount=1500.50"
            + "&payhere_currency=LKR&status_code=2&md5sig=0123456789ABCDEF0123456789ABCDEF&padding=";

        Assert.True(Head.Length < bytes, "The head alone is longer than the size asked for.");

        return Head + new string('a', bytes - Head.Length);
    }

    /// <summary>
    /// The bytes a chunked body of <paramref name="payload"/> bytes occupies on the wire when written in
    /// <see cref="UnsizedContent"/>'s chunks: each is its hexadecimal length, CRLF, the data and CRLF, then the
    /// terminating zero-length chunk (the five bytes 0, CR, LF, CR, LF).
    /// </summary>
    private static int ChunkedWireSize(int payload)
    {
        var total = 5;

        for (var offset = 0; offset < payload; offset += UnsizedContent.ChunkBytes)
        {
            var length = Math.Min(UnsizedContent.ChunkBytes, payload - offset);

            total += length.ToString("x", System.Globalization.CultureInfo.InvariantCulture).Length + 2 + length + 2;
        }

        return total;
    }

    private async Task<HttpResponseMessage> PostAsync(string body, bool chunked)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(PayHereRoutes.NotifyPath, UriKind.Relative))
        {
            Content = chunked ? new UnsizedContent(body) : new StringContent(body, Encoding.ASCII),
        };

        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");

        // Started on first use, like the wishlist's transport tests: no constructor that blocks on a socket.
        _host ??= await KestrelApiHost.StartPayHereNotifyAsync(_handler);
        _client ??= _host.CreateClient();

        // Read in full inside this method, so a test cannot observe a response after the request is gone.
        return await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, Token);
    }

    /// <summary>
    /// A body that does not know its own length, so HttpClient sends it with <c>Transfer-Encoding: chunked</c>
    /// and no <c>Content-Length</c> - the shape that a size check on the header cannot see.
    /// </summary>
    private sealed class UnsizedContent(string text) : HttpContent
    {
        /// <summary>The size of each write, and so of each chunk.</summary>
        public const int ChunkBytes = 2048;

        private readonly byte[] _bytes = Encoding.ASCII.GetBytes(text);

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            // In several writes, so there really are several chunks.
            for (var offset = 0; offset < _bytes.Length; offset += ChunkBytes)
            {
                await stream.WriteAsync(_bytes.AsMemory(offset, Math.Min(ChunkBytes, _bytes.Length - offset)));
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    /// <summary>Counts what reached it and keeps the fields of the last request.</summary>
    private sealed class CountingHandler : ICommandHandler<PayHereNotifyCommand, PayHereNotifyResult>
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyDictionary<string, string>? LastFields { get; private set; }

        public Task<PayHereNotifyResult> HandleAsync(PayHereNotifyCommand command, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            LastFields = command.Fields;

            return Task.FromResult<PayHereNotifyResult>(new PayHereNotifyResult.Accepted(IsDuplicate: false, Outcome: null));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();

        if (_host is not null)
        {
            await _host.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}
