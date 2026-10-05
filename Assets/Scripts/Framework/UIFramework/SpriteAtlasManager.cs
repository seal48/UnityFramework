using System;
using System.Collections.Generic;
using GameFramework.Config;
using GameFramework.Core;
using GameFramework.Log;
using GameFramework.Resource;
using UnityEngine;
using UnityEngine.U2D;

namespace GameFramework.UI
{
    /// <summary>
    /// UI 图集管理器：按逻辑 ID 从配置表取图，减少 draw call。
    ///
    /// **表驱动**：图集 / 图片名不写死在代码里，而是放在配置表 <c>UISprite.xlsx</c>
    /// （Id → Atlas 图集名 + Sprite 图集内图片名）。业务代码只写逻辑 ID：
    ///
    ///   SpriteAtlasManager atlases = GameController.Instance.SpriteAtlases;
    ///   Sprite s = atlases.GetSpriteById("item_icon_sword");          // 已加载则同步取
    ///   atlases.GetSpriteByIdAsync("item_icon_sword", s => { ... });  // 未加载先加载图集
    ///
    /// 底层也保留按名直取：<see cref="GetSprite(string, string)"/>（图集名 + 图片名），
    /// 一般业务用表驱动版本就够了。
    ///
    /// 图集资产在 <c>Assets/UI/Atlas/&lt;图集名&gt;.spriteatlas</c>（编辑器菜单
    /// <c>Tools/UI/图集/从目录创建图集</c> 从一个目录打包生成）。
    /// 图集在 YooAsset bundle 里，走远端下载 = **资源热更**（改图不用发版）。
    /// 缓存保持图集引用（不释放），Shutdown 时统一释放。
    /// </summary>
    public sealed class SpriteAtlasManager : IGameModule
    {
        /// <summary>图集资产根目录（相对 Assets）。每个子目录一张图集。</summary>
        public const string AtlasRoot = "Assets/UI/Atlas/";

        private IResourceService resource;
        private ConfigManager config;
        private readonly Dictionary<string, ResourceAsset<SpriteAtlas>> atlases =
            new Dictionary<string, ResourceAsset<SpriteAtlas>>(StringComparer.Ordinal);
        private bool shutdown;

        /// <summary>是否已初始化（资源服务 / 配置表可用且未关闭）。</summary>
        public bool IsInitialized { get { return resource != null && config != null && !shutdown; } }

        /// <summary>已预加载的图集名。</summary>
        public IReadOnlyCollection<string> LoadedAtlasNames { get { return atlases.Keys; } }

        /// <summary>初始化。配置表与资源服务就绪后调用（ProcedureInitUI 里，和 UI 一起）。</summary>
        public void Init(IResourceService resourceService, ConfigManager configManager)
        {
            resource = resourceService;
            config = configManager;
        }

        /// <summary>图集资产地址（按约定拼）。</summary>
        public static string AtlasLocation(string atlasName)
        {
            return AtlasRoot + atlasName + ".spriteatlas";
        }

        /// <summary>
        /// 预加载一张图集并缓存。重复调用只加载一次。
        /// 加载失败会回回调 false（地址不对 / 资源没收集进包）。
        /// </summary>
        public void PreloadAsync(string atlasName, Action<bool> onComplete)
        {
            if (shutdown || resource == null)
            {
                if (onComplete != null) onComplete(false);
                return;
            }

            if (atlases.ContainsKey(atlasName))
            {
                if (onComplete != null) onComplete(true);
                return;
            }

            string location = AtlasLocation(atlasName);
            resource.LoadAssetAsync<SpriteAtlas>(location, asset =>
            {
                if (shutdown || asset == null || asset.Asset == null)
                {
                    if (asset != null) asset.Dispose();
                    if (onComplete != null) onComplete(false);
                    return;
                }

                atlases[atlasName] = asset;   // 持有引用，Shutdown 时统一释放
                if (onComplete != null) onComplete(true);
            });
        }

        /// <summary>
        /// 同步取图。仅当图集已预加载过才有结果，否则返回 null
        /// （先 <see cref="PreloadAsync"/> 或直接用 <see cref="GetSpriteAsync"/>）。
        /// </summary>
        public Sprite GetSprite(string atlasName, string spriteName)
        {
            ResourceAsset<SpriteAtlas> ra;
            if (atlases.TryGetValue(atlasName, out ra) && ra != null && ra.Asset != null)
                return ra.Asset.GetSprite(spriteName);
            return null;
        }

        /// <summary>异步取图：图集没加载先加载，再回调 Sprite（找不到/失败回 null）。</summary>
        public void GetSpriteAsync(string atlasName, string spriteName, Action<Sprite> onLoaded)
        {
            Sprite quick = GetSprite(atlasName, spriteName);
            if (quick != null)
            {
                if (onLoaded != null) onLoaded(quick);
                return;
            }

            PreloadAsync(atlasName, ok =>
            {
                if (onLoaded == null) return;
                onLoaded(ok ? GetSprite(atlasName, spriteName) : null);
            });
        }

        /// <summary>
        /// 按逻辑 ID 从配置表取图（推荐 API）。查 UISprite.xlsx：Id → (图集名, 图片名)。
        /// 图集已加载时同步返回，否则返回 null（用 <see cref="GetSpriteByIdAsync"/> 或先 Preload）。
        /// </summary>
        public Sprite GetSpriteById(string id)
        {
            UISpriteConfig row = FindRow(id);
            if (row == null) return null;
            return GetSprite(row.Atlas, row.Sprite);
        }

        /// <summary>按逻辑 ID 异步取图：图集没加载先加载，再回调 Sprite（表里没有/失败回 null）。</summary>
        public void GetSpriteByIdAsync(string id, Action<Sprite> onLoaded)
        {
            UISpriteConfig row = FindRow(id);
            if (row == null)
            {
                GameLog.Warn(LogTag.UIManager, "[SpriteAtlas] 配置表 UISprite 里没有 ID：" + id);
                if (onLoaded != null) onLoaded(null);
                return;
            }

            GetSpriteAsync(row.Atlas, row.Sprite, onLoaded);
        }

        private UISpriteConfig FindRow(string id)
        {
            if (config == null || string.IsNullOrEmpty(id)) return null;
            ConfigDatabase db = config.Database;
            if (db == null || db.UISprite == null) return null;
            return db.UISprite.Get(id);
        }

        /// <summary>释放全部缓存的图集。幂等。</summary>
        public void Shutdown()
        {
            if (shutdown) return;
            shutdown = true;

            foreach (KeyValuePair<string, ResourceAsset<SpriteAtlas>> kv in atlases)
            {
                if (kv.Value != null) kv.Value.Dispose();
            }
            atlases.Clear();
            resource = null;
        }
    }
}
