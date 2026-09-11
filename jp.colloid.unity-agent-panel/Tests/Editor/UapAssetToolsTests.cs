using Colloid.AgentPanel.Core.Json;
using Colloid.AgentPanel.Ops;
using NUnit.Framework;
using UnityEditor;

namespace Colloid.AgentPanel.Tests
{
    /// <summary>uap_asset_create / uap_asset_delete / uap_asset_find real-AssetDatabase tests, confined to a scratch subfolder cleaned up afterward.</summary>
    [TestFixture]
    public class UapAssetToolsTests
    {
        private const string TestFolder = "Assets/UapAssetToolsTests_Scratch";

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(TestFolder))
            {
                AssetDatabase.DeleteAsset(TestFolder);
            }
        }

        [Test]
        public void AssetCreate_Material_CreatesAssetAtPath_AutoCreatingFolders()
        {
            var tool = new UapAssetCreateTool();
            string path = TestFolder + "/Sub/Red.mat";

            tool.Execute(JsonNode.NewObject().Set("assetType", "Material").Set("path", path));

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(path);
            Assert.IsNotNull(asset, "Material asset should exist at the requested path.");
        }

        [Test]
        public void AssetCreate_UnknownAssetType_Throws()
        {
            var tool = new UapAssetCreateTool();
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                tool.Execute(JsonNode.NewObject()
                    .Set("assetType", "NotARealScriptableObjectType12345")
                    .Set("path", TestFolder + "/Bad.asset"));
            });
        }

        [Test]
        public void AssetCreate_PathOutsideAssets_Throws()
        {
            var tool = new UapAssetCreateTool();
            Assert.Throws<System.ArgumentException>(delegate
            {
                tool.Execute(JsonNode.NewObject().Set("assetType", "Material").Set("path", "Packages/Foo.mat"));
            });
        }

        /// <summary>
        /// OPS-3 (SR-asset-path): the old bare StartsWith("Assets/") check
        /// let "Assets/../Evil.mat" through -- the string starts with
        /// Assets/ but RESOLVES to the project root. Dot segments now
        /// collapse before the check.
        /// </summary>
        [Test]
        public void AssetCreate_DotDotEscapingAssets_Throws()
        {
            var tool = new UapAssetCreateTool();
            var ex = Assert.Throws<System.ArgumentException>(delegate
            {
                tool.Execute(JsonNode.NewObject()
                    .Set("assetType", "Material")
                    .Set("path", "Assets/../EvilEscape12345.mat"));
            });
            StringAssert.Contains("Assets/", ex.Message);
        }

        [Test]
        public void AssetCreate_DeepDotDotEscape_Throws()
        {
            var tool = new UapAssetCreateTool();
            var ex = Assert.Throws<System.ArgumentException>(delegate
            {
                tool.Execute(JsonNode.NewObject()
                    .Set("assetType", "Material")
                    .Set("path", "Assets/Sub/../../Outside12345.mat"));
            });
            StringAssert.Contains("Assets/", ex.Message);
        }

        /// <summary>
        /// OPS-3's guard must not over-block: dot segments that RESOLVE to
        /// a location still under Assets/ are legitimate -- they collapse
        /// and the asset lands at the normalized path.
        /// </summary>
        [Test]
        public void AssetCreate_DotSegmentsStayingInsideAssets_CollapseAndCreate()
        {
            var tool = new UapAssetCreateTool();

            tool.Execute(JsonNode.NewObject().Set("assetType", "Material")
                .Set("path", TestFolder + "/Sub/./Dot.mat"));
            tool.Execute(JsonNode.NewObject().Set("assetType", "Material")
                .Set("path", TestFolder + "/Sub/../Popped.mat"));

            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(TestFolder + "/Sub/Dot.mat"),
                "'.' segments collapse; the asset lands at the normalized path");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(TestFolder + "/Popped.mat"),
                "a '..' that still resolves under Assets/ is allowed, not blanket-rejected");
        }

        [Test]
        public void AssetCreate_CollidingPath_IsUniquified()
        {
            var tool = new UapAssetCreateTool();
            string path = TestFolder + "/Dup.mat";
            tool.Execute(JsonNode.NewObject().Set("assetType", "Material").Set("path", path));
            tool.Execute(JsonNode.NewObject().Set("assetType", "Material").Set("path", path));

            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(TestFolder + "/Dup.mat"));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(TestFolder + "/Dup 1.mat"));
        }

        [Test]
        public void AssetDelete_MovesAssetToTrash()
        {
            string path = TestFolder + "/ToDelete.mat";
            new UapAssetCreateTool().Execute(JsonNode.NewObject().Set("assetType", "Material").Set("path", path));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(path));

            new UapAssetDeleteTool().Execute(JsonNode.NewObject().Set("path", path).Set(UapDestructiveToolBase.ConfirmKey, true));

            Assert.IsNull(AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(path));
        }

        /// <summary>2026-09-09 destructive gate: no confirm -> refused with a preview, asset untouched.</summary>
        [Test]
        public void AssetDelete_WithoutConfirm_RefusesWithPreview_AndLeavesTheAsset()
        {
            string path = TestFolder + "/KeepMe.mat";
            new UapAssetCreateTool().Execute(JsonNode.NewObject().Set("assetType", "Material").Set("path", path));

            var ex = Assert.Throws<System.InvalidOperationException>(delegate
            {
                new UapAssetDeleteTool().Execute(JsonNode.NewObject().Set("path", path));
            });

            StringAssert.Contains("confirm:true", ex.Message);
            StringAssert.Contains("Would move the Material '" + path + "'", ex.Message);
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(path), "a refused delete must not trash the asset");
        }

        [Test]
        public void AssetDelete_DryRun_OnFolder_CountsAssetsInside_AndChangesNothing()
        {
            new UapAssetCreateTool().Execute(JsonNode.NewObject().Set("assetType", "Material").Set("path", TestFolder + "/Dry/A.mat"));
            new UapAssetCreateTool().Execute(JsonNode.NewObject().Set("assetType", "Material").Set("path", TestFolder + "/Dry/B.mat"));

            JsonNode result = new UapAssetDeleteTool().Execute(JsonNode.NewObject()
                .Set("path", TestFolder + "/Dry")
                .Set(UapDestructiveToolBase.DryRunKey, true));

            string text = result[0]["text"].AsString();
            StringAssert.StartsWith("DRY RUN", text);
            StringAssert.Contains("FOLDER", text);
            StringAssert.IsMatch(@"the [2-9]\d* asset\(s\) inside", text);
            Assert.IsTrue(AssetDatabase.IsValidFolder(TestFolder + "/Dry"), "dry_run must not trash the folder");
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<UnityEngine.Material>(TestFolder + "/Dry/A.mat"));
        }

        /// <summary>OPS-L2 (SR-asset-path): deletion gets the same Assets/ scope guard as creation.</summary>
        [Test]
        public void AssetDelete_PathOutsideAssets_Throws()
        {
            var tool = new UapAssetDeleteTool();
            Assert.Throws<System.ArgumentException>(delegate
            {
                tool.Execute(JsonNode.NewObject().Set("path", "Packages/some.pkg/Bar.asset"));
            });
            Assert.Throws<System.ArgumentException>(delegate
            {
                tool.Execute(JsonNode.NewObject().Set("path", "ProjectSettings/ProjectSettings.asset"));
            });
        }

        [Test]
        public void AssetDelete_DotDotEscapingAssets_Throws()
        {
            var tool = new UapAssetDeleteTool();
            var ex = Assert.Throws<System.ArgumentException>(delegate
            {
                tool.Execute(JsonNode.NewObject().Set("path", "Assets/../ProjectSettings/ProjectSettings.asset"));
            });
            StringAssert.Contains("Assets/", ex.Message);
        }

        [Test]
        public void AssetDelete_NonExistentPath_Throws()
        {
            var tool = new UapAssetDeleteTool();
            Assert.Throws<System.InvalidOperationException>(delegate
            {
                tool.Execute(JsonNode.NewObject().Set("path", TestFolder + "/DoesNotExist12345.mat"));
            });
        }

        [Test]
        public void AssetDelete_MissingPath_Throws()
        {
            var tool = new UapAssetDeleteTool();
            Assert.Throws<System.ArgumentException>(delegate { tool.Execute(JsonNode.NewObject()); });
        }

        [Test]
        public void AssetFind_FindsCreatedMaterialByName()
        {
            string path = TestFolder + "/FindMeUnique98765.mat";
            new UapAssetCreateTool().Execute(JsonNode.NewObject().Set("assetType", "Material").Set("path", path));

            var tool = new UapAssetFindTool();
            JsonNode result = tool.Execute(JsonNode.NewObject().Set("query", "FindMeUnique98765"));
            string text = result[0]["text"].AsString();
            StringAssert.Contains("FindMeUnique98765.mat", text);
        }

        [Test]
        public void AssetFind_MissingQuery_Throws()
        {
            var tool = new UapAssetFindTool();
            Assert.Throws<System.ArgumentException>(delegate { tool.Execute(JsonNode.NewObject()); });
        }
    }
}
