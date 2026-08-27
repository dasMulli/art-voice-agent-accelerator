using Azure;
using Azure.Core;
using Azure.Communication.PhoneNumbers;
using System.Reflection;
using ServiceDeskCallSimulator.PhoneNumbers;

namespace ServiceDeskCallSimulator.Tests;

public sealed class AcsPhoneNumberDiscoveryTests
{
    [Fact]
    public async Task DiscoverAsync_RejectsMalformedPurchasedPhoneNumbers()
    {
        var discovery = new AcsPhoneNumberDiscovery(
            new TestPhoneNumbersClient(
                CreatePurchasedPhoneNumber("+43 800223359", PhoneNumberCapabilityType.Outbound)),
            "+43800223359");

        await Assert.ThrowsAsync<ArgumentException>(() => discovery.DiscoverAsync());
    }

    private static PurchasedPhoneNumber CreatePurchasedPhoneNumber(
        string phoneNumber,
        PhoneNumberCapabilityType callingCapability)
    {
        return CreateInstance<PurchasedPhoneNumber>(
            "11234567890",
            phoneNumber,
            "AT",
            PhoneNumberType.Geographic,
            new PhoneNumberCapabilities(callingCapability, PhoneNumberCapabilityType.None),
            PhoneNumberAssignmentType.Application,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            CreateInstance<PhoneNumberCost>(1.23, "EUR", BillingFrequency.Monthly));
    }

    private static T CreateInstance<T>(params object?[] args)
    {
        return (T)Activator.CreateInstance(
            typeof(T),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: args,
            culture: null)!;
    }

    private sealed class TestPhoneNumbersClient : PhoneNumbersClient
    {
        private readonly AsyncPageable<PurchasedPhoneNumber> _phoneNumbers;

        public TestPhoneNumbersClient(params PurchasedPhoneNumber[] purchasedPhoneNumbers)
            : base(
                new Uri("https://example.invalid/"),
                new global::Azure.AzureKeyCredential("fake-key"),
                new PhoneNumbersClientOptions())
        {
            _phoneNumbers = new SinglePageAsyncPageable<PurchasedPhoneNumber>(purchasedPhoneNumbers);
        }

        public override AsyncPageable<PurchasedPhoneNumber> GetPurchasedPhoneNumbersAsync(
            CancellationToken cancellationToken = default)
        {
            return _phoneNumbers;
        }
    }

    private sealed class SinglePageAsyncPageable<T> : AsyncPageable<T>
        where T : notnull
    {
        private readonly IReadOnlyList<T> _values;

        public SinglePageAsyncPageable(IReadOnlyList<T> values)
        {
            _values = values;
        }

        public override IAsyncEnumerable<Page<T>> AsPages(
            string? continuationToken = null,
            int? pageSizeHint = null)
        {
            return YieldPages();

            async IAsyncEnumerable<Page<T>> YieldPages()
            {
                yield return Page<T>.FromValues(_values, continuationToken, new TestResponse());
                await Task.CompletedTask;
            }
        }
    }

    private sealed class TestResponse : Response
    {
        private Stream? _contentStream = Stream.Null;
        private string _clientRequestId = string.Empty;

        public override int Status => 200;

        public override string ReasonPhrase => "OK";

        public override Stream? ContentStream
        {
            get => _contentStream;
            set => _contentStream = value;
        }

        public override string ClientRequestId
        {
            get => _clientRequestId;
            set => _clientRequestId = value;
        }

        public override ResponseHeaders Headers => default;

        public override void Dispose()
        {
        }

        protected override bool TryGetHeader(string name, out string value)
        {
            value = string.Empty;
            return false;
        }

        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values)
        {
            values = Array.Empty<string>();
            return false;
        }

        protected override bool ContainsHeader(string name)
        {
            return false;
        }

        protected override IEnumerable<HttpHeader> EnumerateHeaders()
        {
            return Array.Empty<HttpHeader>();
        }
    }
}
