namespace ScanApp.Core.Tests.Combine;

/// <summary>
/// The multi-threaded aligner and combine tests share one collection, so they run one class at a time instead of
/// oversubscribing the CPU. Both are also <c>[Trait("Category", "Slow")]</c>.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SlowCombineCollection
{
    public const string Name = "Slow combine";
}
