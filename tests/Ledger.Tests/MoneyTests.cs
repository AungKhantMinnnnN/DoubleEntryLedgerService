using FsCheck;
using FsCheck.Xunit;
using Ledger.Api.Domain;

namespace Ledger.Tests;

public class MoneyTests
{
    [Property]
    public bool Rounded_values_fit_scale_and_are_stable(decimal v) =>
        Math.Abs(v) > 1e15m || (Money.FitsScale(Money.Round(v)) && Money.Round(Money.Round(v)) == Money.Round(v));

    [Property]
    public bool Scaled_integers_are_exact_and_sum_exactly(int[] cents) =>
        cents.All(c => Money.FitsScale(c / 10000m)) &&
        cents.Sum(c => (long)c) / 10000m == cents.Sum(c => c / 10000m);

    [Fact]
    public void Banker_rounding_at_midpoint() => Assert.Equal(0.1234m, Money.Round(0.12345m)); // ToEven
}
