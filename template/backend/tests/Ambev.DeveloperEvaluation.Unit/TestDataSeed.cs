using System.Runtime.CompilerServices;
using Bogus;

namespace Ambev.DeveloperEvaluation.Unit;

/// <summary>
/// Seeds Bogus's global <see cref="Randomizer.Seed"/> at assembly load
/// time so every Faker created by the test data builders produces the
/// same sequence on every run. A failing test on CI is reproducible
/// locally by checking out the same commit and running it.
/// </summary>
internal static class TestDataSeed
{
    [ModuleInitializer]
    internal static void Initialise()
    {
        // Deterministic seed — any non-zero value works. Change only when
        // intentionally regenerating fixture data.
        Randomizer.Seed = new Random(0xC0FFEE);
    }
}
