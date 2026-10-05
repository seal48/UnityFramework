using System;
using GameFramework.Event;
using GameFramework.Log;
using GameFramework.Resource;
using UnityEngine;
using UnityEngine.UI;

namespace GameFramework.Localization
{
    /// <summary>
    /// 本地化元素：挂到带 Text / Image 的节点上，填一个 key，
    /// 语言切换（LanguageChangedEvent）时自动刷新文本 + 图片 + 字体。
    ///
    /// 约定：文本和图片默认共用同一个 key；个别元素图片要单独换时，填 imageKey 覆盖。
    /// 纯文本节点（没有 Image）或纯图片节点（没有 Text）都行，按有的组件刷新。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class LocalizedElement : MonoBehaviour
    {
        [Tooltip("本地化 key：文本和图片都用它（跨语言唯一）")]
        public string key;

        [Tooltip("图片 key：留空 = 用 key 的图片；文本和图片不同 key 时填这个")]
        public string imageKey;

        private Text _text;
        private Image _image;
        private IDisposable _sub;
        private ResourceAsset<Sprite> _spriteAsset;
        private string _loadedImageAddress;

        private void Awake()
        {
            _text = GetComponent<Text>();
            _image = GetComponent<Image>();
        }

        private void OnEnable()
        {
            var loc = LocalizationManager.Current;
            var bus = EventBus.Global;
            if (loc == null || bus == null)
            {
                GameLog.Warn(LogTag.Localization, "LocalizedElement 在本地化初始化之前启用（" + name + "），已跳过。");
                return;
            }

            _sub = bus.Subscribe<LanguageChangedEvent>(_ => Apply());
            Apply();
        }

        private void OnDisable()
        {
            if (_sub != null)
            {
                _sub.Dispose();
                _sub = null;
            }
            // 不释放图片：面板缓存（隐藏）期间保留，避免反复加载
        }

        private void OnDestroy()
        {
            ReleaseSprite();
        }

        private void Apply()
        {
            var loc = LocalizationManager.Current;
            if (loc == null)
                return;

            if (_text != null)
            {
                _text.text = loc.GetText(key);
                if (loc.Font != null)
                    _text.font = loc.Font;
            }

            if (_image != null)
            {
                string addr = loc.GetImageAddress(string.IsNullOrEmpty(imageKey) ? key : imageKey);
                if (string.IsNullOrEmpty(addr))
                {
                    ReleaseSprite();
                    _image.sprite = null;
                    return;
                }

                // 同一地址已经加载过了：不用重载
                if (addr == _loadedImageAddress && _spriteAsset != null)
                    return;

                LoadImage(addr);
            }
        }

        private void LoadImage(string addr)
        {
            ReleaseSprite();
            _loadedImageAddress = addr;

            var loc = LocalizationManager.Current;
            var resource = loc != null ? loc.Resource : null;
            if (resource == null)
            {
                GameLog.Error(LogTag.Localization, "资源服务不可用，无法加载本地化图片：" + addr);
                return;
            }

            resource.LoadAssetAsync<Sprite>(addr, asset =>
            {
                if (asset == null || this == null)
                {
                    if (asset != null)
                        asset.Dispose();
                    return;
                }

                // 加载期间语言 / key 又变了：丢弃这次结果（地址对不上）
                if (_loadedImageAddress != asset.Location)
                {
                    asset.Dispose();
                    return;
                }

                _spriteAsset = asset;
                if (_image != null)
                    _image.sprite = asset.Asset;
            });
        }

        private void ReleaseSprite()
        {
            _loadedImageAddress = null;
            if (_spriteAsset != null)
            {
                _spriteAsset.Dispose();
                _spriteAsset = null;
            }
        }
    }
}
