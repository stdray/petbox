namespace YobaBox.E2ETests.Infrastructure;

[CollectionDefinition(nameof(UiCollection))]
public sealed class UiCollection : ICollectionFixture<WebAppFixture>;
