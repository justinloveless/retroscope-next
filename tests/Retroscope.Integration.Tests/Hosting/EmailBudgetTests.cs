using System.Globalization;
using System.Net;
using Microsoft.Extensions.Time.Testing;
using Retroscope.Core.Ids;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Hosting;

[Trait("Category", "Hosting")]
public sealed class EmailBudgetTests {
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-20T12:00:00Z", CultureInfo.InvariantCulture));
    private readonly EmailBudget _budget;
    private int _ip;

    public EmailBudgetTests() {
        _budget = new EmailBudget(new RateLimiter(_time));
    }

    [Fact]
    public void Three_mails_per_recipient_per_hour() {
        for (var i = 0; i < 3; i++) {
            Assert.True(_budget.TryReserve("ada@example.com", FreshIp(), null));
        }

        Assert.False(_budget.TryReserve(" ADA@example.com ", FreshIp(), null));
        Assert.True(_budget.TryReserve("bob@example.com", FreshIp(), null));
        _time.Advance(TimeSpan.FromMinutes(20));
        Assert.True(_budget.TryReserve("ada@example.com", FreshIp(), null));
        Assert.Equal(1, _budget.Dropped);
    }

    [Fact]
    public void Twenty_mails_per_ip_per_hour() {
        var ip = IPAddress.Parse("198.51.100.7");
        for (var i = 0; i < 20; i++) {
            Assert.True(_budget.TryReserve($"r{i}@example.com", ip, null));
        }

        Assert.False(_budget.TryReserve("r20@example.com", ip, null));
        Assert.False(_budget.TryReserve("r21@example.com", IPAddress.Parse("::ffff:198.51.100.7"), null));
        Assert.True(_budget.TryReserve("r22@example.com", FreshIp(), null));
    }

    [Fact]
    public void Twenty_mails_per_sending_user_per_day() {
        var sender = new PrincipalId(Guid.CreateVersion7());
        for (var i = 0; i < 20; i++) {
            Assert.True(_budget.TryReserve($"r{i}@example.com", FreshIp(), sender));
        }

        Assert.False(_budget.TryReserve("r20@example.com", FreshIp(), sender));
        _time.Advance(TimeSpan.FromHours(1));
        Assert.False(_budget.TryReserve("r21@example.com", FreshIp(), sender));
        _time.Advance(TimeSpan.FromHours(1.3));
        Assert.True(_budget.TryReserve("r22@example.com", FreshIp(), sender));
    }

    [Fact]
    public void Two_hundred_mails_per_hour_in_total() {
        for (var i = 0; i < 200; i++) {
            Assert.True(_budget.TryReserve($"r{i}@example.com", FreshIp(), null));
        }

        Assert.False(_budget.TryReserve("r200@example.com", FreshIp(), null));
        Assert.False(_budget.TryReserve("r201@example.com", FreshIp(), new PrincipalId(Guid.CreateVersion7())));
        Assert.Equal(2, _budget.Dropped);
        _time.Advance(TimeSpan.FromSeconds(18));
        Assert.True(_budget.TryReserve("r202@example.com", FreshIp(), null));
    }

    [Fact]
    public void A_dropped_mail_is_not_charged_to_the_other_budgets() {
        var ip = IPAddress.Parse("198.51.100.8");
        for (var i = 0; i < 3; i++) {
            Assert.True(_budget.TryReserve("ada@example.com", ip, null));
        }
        for (var i = 0; i < 30; i++) {
            Assert.False(_budget.TryReserve("ada@example.com", ip, null));
        }

        for (var i = 0; i < 17; i++) {
            Assert.True(_budget.TryReserve($"r{i}@example.com", ip, null));
        }
        Assert.False(_budget.TryReserve("r17@example.com", ip, null));
    }

    private IPAddress FreshIp() {
        _ip++;
        return new IPAddress([10, (byte)(_ip >> 16), (byte)(_ip >> 8), (byte)_ip]);
    }
}
