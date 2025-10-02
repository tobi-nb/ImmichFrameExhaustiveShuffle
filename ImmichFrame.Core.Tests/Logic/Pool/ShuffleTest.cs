using NUnit.Framework;
using Moq;
using ImmichFrame.Core.Interfaces;
using ImmichFrame.Core.Logic;
using ImmichFrame.Core.Logic.Pool;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ImmichFrame.Core.Tests.Logic
{
    [TestFixture]
    public class PooledImmichFrameLogicTests
    {
        private Mock<IAccountSettings> _mockAccountSettings;
        private Mock<IGeneralSettings> _mockGeneralSettings;
        private Mock<IAssetPool> _mockAssetPool;
        private PooledImmichFrameLogic _logic;

        [SetUp]
        public void Setup()
        {
            _mockAccountSettings = new Mock<IAccountSettings>();
            _mockGeneralSettings = new Mock<IGeneralSettings>();
            _mockAssetPool = new Mock<IAssetPool>();

            // Default: ExhaustiveShuffle = true
            _mockGeneralSettings.SetupGet(s => s.ExhaustiveShuffle).Returns(true);

            // Mock Pool mit festen Assets
            var assets = Enumerable.Range(1, 5)
                .Select(i => new AssetResponseDto { Id = i.ToString(), Type = AssetTypeEnum.IMAGE })
                .ToList();

            _mockAssetPool.Setup(p => p.GetAssetCount(default))
                .ReturnsAsync(assets.Count);
            _mockAssetPool.Setup(p => p.GetAssets(It.IsAny<int>(), default))
                .ReturnsAsync((int count, System.Threading.CancellationToken _) => assets);

            // Da PooledImmichFrameLogic den Pool im Konstruktor selbst baut,
            // tricksen wir ein wenig: wir erben eine Testklasse, die unseren Mock-Pool zurückgibt
            _logic = new TestablePooledImmichFrameLogic(
                _mockAccountSettings.Object,
                _mockGeneralSettings.Object,
                _mockAssetPool.Object
            );
        }

        [Test]
        public async Task ExhaustiveShuffle_ShouldReturnAllAssetsBeforeRepeating()
        {
            var seen = new HashSet<string>();

            // Erste 5 Aufrufe → alle 5 unterschiedlichen IDs
            for (int i = 0; i < 5; i++)
            {
                var asset = await _logic.GetNextAsset();
                Assert.That(asset, Is.Not.Null);
                Assert.That(seen.Add(asset!.Id), Is.True, $"Duplicate ID {asset.Id} before exhausting all assets");
            }

            // Danach darf wiederholt werden
            var next = await _logic.GetNextAsset();
            Assert.That(next, Is.Not.Null);
            Assert.That(seen.Contains(next!.Id), Is.True);
        }

        // Hilfsklasse um den Pool zu überschreiben
        private class TestablePooledImmichFrameLogic : PooledImmichFrameLogic
        {
            private readonly IAssetPool _testPool;

            public TestablePooledImmichFrameLogic(IAccountSettings acc, IGeneralSettings gen, IAssetPool testPool)
                : base(acc, gen, new FakeHttpClientFactory())
            {
                _testPool = testPool;
            }

            protected override IAssetPool BuildPool(IAccountSettings accountSettings) => _testPool;
        }

        private class FakeHttpClientFactory : IHttpClientFactory
        {
            public System.Net.Http.HttpClient CreateClient(string name) => new System.Net.Http.HttpClient();
        }
    }
}
