using System;
using System.Collections.Generic;
using UnityEngine;

#if UNITY_ANDROID
using UnityEngine.Android;
#endif

namespace GameFramework.Platform
{
    /// <summary>
    /// Android 运行时权限申请。真机走 UnityEngine.Android.Permission（异步、回调在主线程），
    /// 其它平台（编辑器 / iOS 等）一律当作已授权，方便在编辑器里联调。
    ///
    /// 注意：每个权限都要先在 AndroidManifest 里声明对应的 &lt;uses-permission&gt;，
    /// 否则系统会直接拒绝申请。见本模块 README 的清单说明。
    /// </summary>
    public static class PlatformPermissions
    {
        // ---- 常用权限（Android 权限名）----
        public const string StorageRead = "android.permission.READ_EXTERNAL_STORAGE";
        public const string StorageWrite = "android.permission.WRITE_EXTERNAL_STORAGE";
        public const string Camera = "android.permission.CAMERA";
        public const string Microphone = "android.permission.RECORD_AUDIO";
        public const string FineLocation = "android.permission.ACCESS_FINE_LOCATION";
        public const string CoarseLocation = "android.permission.ACCESS_COARSE_LOCATION";
        public const string PostNotifications = "android.permission.POST_NOTIFICATIONS";
        public const string ReadPhoneState = "android.permission.READ_PHONE_STATE";

        /// <summary>是否已授权。非 Android 平台返回 true。</summary>
        public static bool HasPermission(string permission)
        {
            if (string.IsNullOrEmpty(permission))
                return true;

#if UNITY_ANDROID && !UNITY_EDITOR
            return Permission.HasUserAuthorizedPermission(permission);
#else
            return true;
#endif
        }

        /// <summary>申请一个权限，结果在主线程回调。</summary>
        public static void Request(string permission, Action<PlatformPermissionResult> onResult)
        {
            Request(new[] { permission }, results =>
            {
                if (onResult != null && results != null && results.Count > 0)
                    onResult(results[0]);
            });
        }

        /// <summary>
        /// 批量申请权限。已经授权的会直接进结果列表，需要弹窗的走系统回调；
        /// 全部收集完后在主线程回调一次（顺序不保证，按 Permission 字段区分）。
        /// </summary>
        public static void Request(string[] permissions, Action<IReadOnlyList<PlatformPermissionResult>> onAllDone)
        {
            if (permissions == null || permissions.Length == 0)
            {
                if (onAllDone != null)
                    onAllDone(new List<PlatformPermissionResult>(0));
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            var results = new List<PlatformPermissionResult>(permissions.Length);
            var toAsk = new List<string>(permissions.Length);

            for (int i = 0; i < permissions.Length; i++)
            {
                string permission = permissions[i];
                if (string.IsNullOrEmpty(permission) || Permission.HasUserAuthorizedPermission(permission))
                {
                    results.Add(new PlatformPermissionResult { Permission = permission, Granted = true });
                }
                else
                {
                    toAsk.Add(permission);
                }
            }

            if (toAsk.Count == 0)
            {
                if (onAllDone != null)
                    onAllDone(results);
                return;
            }

            int remaining = toAsk.Count;
            var callbacks = new PermissionCallbacks();

            Action<PlatformPermissionResult> finish = result =>
            {
                results.Add(result);
                remaining--;
                if (remaining == 0 && onAllDone != null)
                    onAllDone(results);
            };

            callbacks.PermissionGranted += permission =>
                finish(new PlatformPermissionResult { Permission = permission, Granted = true });
            callbacks.PermissionDenied += permission =>
                finish(new PlatformPermissionResult { Permission = permission, Granted = false });
            callbacks.PermissionDeniedAndDontAskAgain += permission =>
                finish(new PlatformPermissionResult { Permission = permission, Granted = false, DontAskAgain = true });

            for (int i = 0; i < toAsk.Count; i++)
                Permission.RequestUserPermission(toAsk[i], callbacks);
#else
            var all = new List<PlatformPermissionResult>(permissions.Length);
            for (int i = 0; i < permissions.Length; i++)
                all.Add(new PlatformPermissionResult { Permission = permissions[i], Granted = true });
            if (onAllDone != null)
                onAllDone(all);
#endif
        }
    }
}
