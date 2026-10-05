using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.U2D;
using UnityEngine;
using UnityEngine.U2D;

namespace GameFramework.UI.EditorTools
{
    /// <summary>
    /// UI 图集打包工具。
    ///
    /// 约定：<c>Assets/UI/Atlas/&lt;图集名&gt;/</c> 是一个目录，里面放该图集的所有 Sprite 图片，
    /// 打包成 <c>Assets/UI/Atlas/&lt;图集名&gt;.spriteatlas</c>。
    /// 运行时用 <see cref="SpriteAtlasManager"/> 按名字取图。
    /// </summary>
    public static class UISpriteAtlasTools
    {
        private const string AtlasRoot = "Assets/UI/Atlas";

        [MenuItem("Tools/UI/图集/从目录创建图集", priority = 200)]
        public static void CreateAtlasesFromFolders()
        {
            if (!Directory.Exists(AtlasRoot))
            {
                Directory.CreateDirectory(AtlasRoot);
                AssetDatabase.Refresh();
                Debug.Log("[图集] 已创建 " + AtlasRoot + "，把 Sprite 图片放进 <图集名>/ 子目录再跑一次。");
                return;
            }

            int created = 0;
            foreach (string dir in Directory.GetDirectories(AtlasRoot))
            {
                string name = Path.GetFileName(dir);
                string atlasPath = AtlasRoot + "/" + name + ".spriteatlas";

                SpriteAtlas atlas = AssetDatabase.LoadAssetAtPath<SpriteAtlas>(atlasPath);
                if (atlas == null)
                {
                    // Unity 2021.3 的 SpriteAtlas 不是 ScriptableObject，用 new 创建
                    atlas = new SpriteAtlas();
                    AssetDatabase.CreateAsset(atlas, atlasPath);
                    created++;
                }

                // 把目录下所有 Sprite 加进图集
                AddFolderSprites(atlas, dir);
                EditorUtility.SetDirty(atlas);
            }

            AssetDatabase.SaveAssets();

            // 关键：图集必须「打包」才真正生成图集纹理。
            // 未打包的图集里 Sprite 是占位符，运行时 GetSprite() 会原生崩溃（SIGSEGV）！
            // YooAsset 的 bundle 构建不会自动打包图集，必须在这里显式打包。
            SpriteAtlasUtility.PackAllAtlases(EditorUserBuildSettings.activeBuildTarget, false);

            AssetDatabase.Refresh();
            Debug.Log("[图集] 打包完成，新建 " + created + " 张图集，已强制打包（共 " +
                      Directory.GetDirectories(AtlasRoot).Length + " 个目录）。");
        }

        [MenuItem("Tools/UI/图集/更新全部图集", priority = 201)]
        public static void UpdateAllAtlases()
        {
            CreateAtlasesFromFolders();
            Debug.Log("[图集] 全部图集已更新。");
        }

        [MenuItem("Tools/UI/图集/打开图集目录", priority = 202)]
        public static void OpenAtlasFolder()
        {
            if (!Directory.Exists(AtlasRoot))
            {
                CreateAtlasesFromFolders();
                return;
            }
            EditorUtility.RevealInFinder(Path.GetFullPath(AtlasRoot));
        }

        [MenuItem("Tools/UI/图集/测试取图（需 Play 模式）", priority = 203)]
        public static void TestGetSprite()
        {
            var gc = GameController.Instance;
            if (gc == null || gc.SpriteAtlases == null || !gc.SpriteAtlases.IsInitialized)
            {
                Debug.LogWarning("[图集测试] 需要进入 Play 模式且游戏初始化完成后再点。");
                return;
            }

            // 取 UISprite 表里第一行做演示
            var db = gc.Config.Database;
            if (db == null || db.UISprite == null || db.UISprite.Count == 0)
            {
                Debug.LogWarning("[图集测试] UISprite 表为空。");
                return;
            }

            var row = db.UISprite.Rows[0];
            Debug.Log("[图集测试] 表行: Id=" + row.Id + " Atlas=" + row.Atlas + " Sprite=" + row.Sprite);
            gc.SpriteAtlases.GetSpriteByIdAsync(row.Id, s =>
            {
                Debug.Log("[图集测试] GetSpriteById(\"" + row.Id + "\") => " + (s != null ? s.name : "null（取图失败）"));
            });
        }

        private static void AddFolderSprites(SpriteAtlas atlas, string folder)
        {
            var sprites = new List<Object>();
            foreach (string file in Directory.GetFiles(folder, "*.png", SearchOption.TopDirectoryOnly))
            {
                string assetPath = file.Replace('\\', '/');
                Object[] objs = AssetDatabase.LoadAllAssetsAtPath(assetPath);
                for (int i = 0; i < objs.Length; i++)
                {
                    if (objs[i] is Sprite)
                        sprites.Add(objs[i]);
                }
            }
            atlas.Add(sprites.ToArray());
        }
    }
}
