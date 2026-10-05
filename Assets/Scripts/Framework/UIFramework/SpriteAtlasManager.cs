using System;
using System.Collections.Generic;
using GameFramework.Core;
using GameFramework.Resource;
using UnityEngine;
using UnityEngine.U2D;

namespace GameFramework.UI
{
    /// <summary>
    /// UI 图集管理器：按名字从 SpriteAtlas 取图，减少 draw call。
    ///
    /// 约定：图集资产放在 <c>Assets/UI/Atlas/&lt;图集名&gt;.spriteatlas</c>（编辑器菜单
    /// <c>Tools/UI/图集/从目录创建图集</c> 从一个目录打包生成）。取图地址按
    /// <c>Assets/UI/Atlas/&lt;图集名&gt;.spriteatlas</c>，Sprite 名字就是图集里的资源名。
    ///
    /// 用法：
    ///   SpriteAtlasManager atlases = GameController.Instance.SpriteAtlases;
    ///   atlases.PreloadAsync("Icon", ok => { ... });            // 预加载
    ///   Sprite s = atlases.GetSprite("Icon", "sword");          // 已加载时同步取
    ///   atlases.GetSpriteAsync("Icon", "sword", s => { ... });  // 未加载则先加载再取
    ///
    /// 缓存保持图集引用（不释放），Shutdown 时统一释放。改图集资产后要重建资源包（红线 4）。
    /// </summary>
    public sealed class SpriteAtlasManager : IGameModule
    {
        /// <summary>图集资产根目录（相对 Assets）。每个子目录一张图集。</summary>
        public const string AtlasRoot = "Assets/UI/Atlas/";

        private IResourceService resource;
        private readonly Dictionary<string, ResourceAsset<SpriteAtlas>> atlases =
            new Dictionary<string, ResourceAsset<SpriteAtlas>>(StringComparer.Ordinal);
        private bool shutdown;

        /// <summary>是否已初始化（资源服务可用且未关闭）。</summary>
        public bool IsInitialized { get { return resource != null && !shutdown; } }

        /// <summary>已预加载的图集名。</summary>
        public IReadOnlyCollection<string> LoadedAtlasNames { get { return atlases.Keys; } }

        /// <summary>初始化。资源服务就绪后调用（ProcedureInitUI 里，和 UI 一起）。</summary>
        public void Init(IResourceService resourceService)
        {
            resource = resourceService;
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
