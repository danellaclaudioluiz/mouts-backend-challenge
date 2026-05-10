using System.Runtime.CompilerServices;
using Bogus;

namespace Ambev.DeveloperEvaluation.Integration;

/// <summary>
/// Seeds Bogus's global <see cref="Randomizer.Seed"/> at assembly load
/// time so test data builders produce reproducible sequences.
/// </summary>
internal static class TestDataSeed
{
    [ModuleInitializer]
    internal static void Initialise()
    {
        Randomizer.Seed = new Random(0xC0FFEE);
    }
}
