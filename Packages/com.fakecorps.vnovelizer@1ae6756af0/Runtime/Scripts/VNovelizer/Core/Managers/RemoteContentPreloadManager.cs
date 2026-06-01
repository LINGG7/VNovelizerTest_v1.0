using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

public class RemoteContentPreloadManager : BaseManager<RemoteContentPreloadManager>
{
    private const string LoadingTaskID = "remote_content_preload";
    private const string LoadingTaskName = "下载CDN资源";
    private const float LoadingTaskWeight = 1f;
    private const float CatalogOperationTimeoutSeconds = 10f;
    private const float RemoteOperationTimeoutSeconds = 180f;

    private readonly HashSet<string> completedLabels = new HashSet<string>();
    private readonly HashSet<string> failedLabels = new HashSet<string>();
    private bool isPreloading;
    private float progress;
    private long totalDownloadBytes;
    private long downloadedBytes;

    public bool IsPreloading => isPreloading;
    public float Progress => progress;
    public long TotalDownloadBytes => totalDownloadBytes;
    public long DownloadedBytes => downloadedBytes;

    public void StartMainMenuPreload()
    {
        if (isPreloading)
        {
            return;
        }

        MonoManager.GetInstance().StartCoroutine(PreloadMainMenuContent());
    }

    private IEnumerator PreloadMainMenuContent()
    {
        isPreloading = true;
        progress = 0f;
        downloadedBytes = 0;
        MonoManager.GetInstance().StartCoroutine(PreloadCommonUIPanels());
#if UNITY_WEBGL && !UNITY_EDITOR
        Debug.Log("[RemoteContentPreload] Skipping runtime catalog update check on WebGL.");
#else
        yield return InitializeAndUpdateCatalogs();
#endif
        RegisterOrUpdateLoadingTask(0f, "准备下载CDN资源");

        List<string> labels = BuildMainMenuPreloadLabels();
        yield return CalculateTotalDownloadSize(labels);

        if (totalDownloadBytes <= 0)
        {
            progress = 1f;
            CompleteLoadingTask("CDN资源已缓存");
            isPreloading = false;
            Debug.Log("[RemoteContentPreload] No remote content needs downloading.");
            yield return PreloadCommonUIPanels();
            yield break;
        }

        RegisterOrUpdateLoadingTask(0f, $"下载CDN资源 0% ({FormatBytes(0)} / {FormatBytes(totalDownloadBytes)})");

        long completedBytesBeforeLabel = 0;

        foreach (string label in labels)
        {
            if (completedLabels.Contains(label))
            {
                continue;
            }

            long labelSize = 0;
            yield return GetDownloadSize(label, size => labelSize = size);

            if (labelSize <= 0)
            {
                completedLabels.Add(label);
                continue;
            }

            yield return DownloadLabel(label, labelSize, completedBytesBeforeLabel);
            completedBytesBeforeLabel += labelSize;
            downloadedBytes = System.Math.Min(totalDownloadBytes, completedBytesBeforeLabel);
            progress = totalDownloadBytes > 0 ? (float)downloadedBytes / totalDownloadBytes : 1f;
            RegisterOrUpdateLoadingTask(progress, BuildProgressTaskName(label));
        }

        progress = 1f;
        CompleteLoadingTask("CDN资源下载完成");
        isPreloading = false;
        Debug.Log($"[RemoteContentPreload] Finished. Downloaded {FormatBytes(downloadedBytes)} / {FormatBytes(totalDownloadBytes)}.");
        yield return PreloadCommonUIPanels();
    }

    private IEnumerator PreloadCommonUIPanels()
    {
        VNProjectConfig config = VNProjectConfig.Instance;
        if (config == null)
        {
            yield break;
        }

        UIManager uiManager = UIManager.GetInstance();
        yield return new WaitForSecondsRealtime(1f);
        uiManager.PreloadPanelPrefab("SettingsPanel", config.UI_SettingsPath);
        yield return new WaitForSecondsRealtime(0.5f);

        uiManager.PreloadPanelPrefab("GalleryPanel", config.UI_GalleryPath);
    }

    private List<string> BuildMainMenuPreloadLabels()
    {
        VNProjectConfig config = VNProjectConfig.Instance;
        List<string> labels = new List<string>();

        AddLabel(labels, config != null ? config.VNScriptResPath : "VNovelizerRes/VNScripts");
        AddLabel(labels, config != null ? config.BackgroundResPath : "VNovelizerRes/Backgrounds");
        AddLabel(labels, "VNovelizerRes/GalleryContent/CG");
        AddLabel(labels, config != null ? config.CharacterResPath : "VNovelizerRes/Characters");
        AddLabel(labels, "VNovelizerRes/CharactersPic");
        AddLabel(labels, config != null ? config.BgmResPath : "VNovelizerRes/Audio/Music/BGM");
        AddLabel(labels, config != null ? config.SFXResPath : "VNovelizerRes/Audio/SFX");
        AddLabel(labels, config != null ? config.VoiceResPath : "VNovelizerRes/Audio/Voice");
        AddLabel(labels, config != null ? config.VideoResPath : "VNovelizerRes/Videos");

        return labels;
    }

    private void AddLabel(List<string> labels, string label)
    {
        if (string.IsNullOrWhiteSpace(label) || labels.Contains(label))
        {
            return;
        }

        labels.Add(label);
    }

    private IEnumerator InitializeAndUpdateCatalogs()
    {
        RegisterOrUpdateLoadingTask(0f, "检查CDN资源目录");

        AsyncOperationHandle initializeHandle = Addressables.InitializeAsync();
        yield return WaitForAddressablesOperation(initializeHandle, CatalogOperationTimeoutSeconds);

        if (!initializeHandle.IsDone)
        {
            Debug.LogWarning("[RemoteContentPreload] Addressables initialization timed out. Skipping catalog update.");
            if (initializeHandle.IsValid())
            {
                Addressables.Release(initializeHandle);
            }
            yield break;
        }

        if (initializeHandle.Status != AsyncOperationStatus.Succeeded)
        {
            Debug.LogWarning($"[RemoteContentPreload] Failed to initialize Addressables: {initializeHandle.OperationException?.Message}");
            yield break;
        }

        AsyncOperationHandle<List<string>> checkHandle = Addressables.CheckForCatalogUpdates(false);
        yield return WaitForAddressablesOperation(checkHandle, CatalogOperationTimeoutSeconds);

        if (!checkHandle.IsDone)
        {
            Debug.LogWarning("[RemoteContentPreload] Catalog update check timed out. Continuing with the currently loaded catalog.");
            if (checkHandle.IsValid())
            {
                Addressables.Release(checkHandle);
            }
            yield break;
        }

        if (checkHandle.Status != AsyncOperationStatus.Succeeded)
        {
            Debug.LogWarning($"[RemoteContentPreload] Failed to check catalog updates: {checkHandle.OperationException?.Message}");
            Addressables.Release(checkHandle);
            yield break;
        }

        List<string> catalogsToUpdate = checkHandle.Result;
        if (catalogsToUpdate == null || catalogsToUpdate.Count == 0)
        {
            Addressables.Release(checkHandle);
            yield break;
        }

        RegisterOrUpdateLoadingTask(0f, "更新CDN资源目录");

        AsyncOperationHandle<List<IResourceLocator>> updateHandle = Addressables.UpdateCatalogs(catalogsToUpdate, false);
        yield return WaitForAddressablesOperation(updateHandle, CatalogOperationTimeoutSeconds);

        if (!updateHandle.IsDone)
        {
            Debug.LogWarning("[RemoteContentPreload] Catalog update timed out. Continuing with the currently loaded catalog.");
            if (updateHandle.IsValid())
            {
                Addressables.Release(updateHandle);
            }
            Addressables.Release(checkHandle);
            yield break;
        }

        if (updateHandle.Status == AsyncOperationStatus.Succeeded)
        {
            completedLabels.Clear();
            failedLabels.Clear();
            ResourcesManager.GetInstance().ReleaseCachedAssets();
            Debug.Log($"[RemoteContentPreload] Updated {catalogsToUpdate.Count} remote catalog(s).");
        }
        else
        {
            Debug.LogWarning($"[RemoteContentPreload] Failed to update catalogs: {updateHandle.OperationException?.Message}");
        }

        Addressables.Release(updateHandle);
        Addressables.Release(checkHandle);
    }

    private IEnumerator WaitForAddressablesOperation(AsyncOperationHandle handle, float timeoutSeconds)
    {
        float startedAt = Time.realtimeSinceStartup;
        while (!handle.IsDone && Time.realtimeSinceStartup - startedAt < timeoutSeconds)
        {
            yield return null;
        }
    }

    private IEnumerator CalculateTotalDownloadSize(List<string> labels)
    {
        totalDownloadBytes = 0;

        foreach (string label in labels)
        {
            if (completedLabels.Contains(label))
            {
                continue;
            }

            long size = 0;
            yield return GetDownloadSize(label, result => size = result);
            totalDownloadBytes += size;
        }

        Debug.Log($"[RemoteContentPreload] Main menu preload size: {FormatBytes(totalDownloadBytes)}.");
    }

    private IEnumerator GetDownloadSize(string label, System.Action<long> callback)
    {
        bool hasLocations = false;
        yield return HasLocations(label, result => hasLocations = result);

        if (!hasLocations)
        {
            callback?.Invoke(0);
            yield break;
        }

        AsyncOperationHandle<long> sizeHandle = Addressables.GetDownloadSizeAsync(label);
        yield return WaitForAddressablesOperation(sizeHandle, CatalogOperationTimeoutSeconds);

        if (!sizeHandle.IsDone)
        {
            Debug.LogWarning($"[RemoteContentPreload] Download size query timed out: {label}");
            if (sizeHandle.IsValid())
            {
                Addressables.Release(sizeHandle);
            }
            callback?.Invoke(0);
            yield break;
        }

        if (sizeHandle.Status == AsyncOperationStatus.Succeeded)
        {
            callback?.Invoke(sizeHandle.Result);
        }
        else
        {
            Debug.LogWarning($"[RemoteContentPreload] Failed to query download size: {label}");
            callback?.Invoke(0);
        }

        Addressables.Release(sizeHandle);
    }

    private IEnumerator HasLocations(string label, System.Action<bool> callback)
    {
        AsyncOperationHandle<IList<IResourceLocation>> locationHandle = Addressables.LoadResourceLocationsAsync(label);
        yield return WaitForAddressablesOperation(locationHandle, CatalogOperationTimeoutSeconds);

        if (!locationHandle.IsDone)
        {
            Debug.LogWarning($"[RemoteContentPreload] Location lookup timed out: {label}");
            if (locationHandle.IsValid())
            {
                Addressables.Release(locationHandle);
            }
            callback?.Invoke(false);
            yield break;
        }

        bool hasLocations =
            locationHandle.Status == AsyncOperationStatus.Succeeded &&
            locationHandle.Result != null &&
            locationHandle.Result.Count > 0;

        Addressables.Release(locationHandle);
        callback?.Invoke(hasLocations);
    }

    private IEnumerator DownloadLabel(string label, long labelSize, long completedBytesBeforeLabel)
    {
        Debug.Log($"[RemoteContentPreload] Downloading {label}: {FormatBytes(labelSize)}.");

        AsyncOperationHandle downloadHandle = Addressables.DownloadDependenciesAsync(label, false);
        float startedAt = Time.realtimeSinceStartup;

        while (!downloadHandle.IsDone && Time.realtimeSinceStartup - startedAt < RemoteOperationTimeoutSeconds)
        {
            downloadedBytes = completedBytesBeforeLabel + (long)(labelSize * downloadHandle.PercentComplete);
            progress = totalDownloadBytes > 0 ? (float)downloadedBytes / totalDownloadBytes : 0f;
            RegisterOrUpdateLoadingTask(progress, BuildProgressTaskName(label));
            yield return null;
        }

        if (!downloadHandle.IsDone)
        {
            failedLabels.Add(label);
            Debug.LogWarning($"[RemoteContentPreload] Download timed out: {label}");
            if (downloadHandle.IsValid())
            {
                Addressables.Release(downloadHandle);
            }
            yield break;
        }

        if (downloadHandle.Status == AsyncOperationStatus.Succeeded)
        {
            completedLabels.Add(label);
            failedLabels.Remove(label);
            Debug.Log($"[RemoteContentPreload] Downloaded {label}.");
        }
        else
        {
            failedLabels.Add(label);
            Debug.LogWarning($"[RemoteContentPreload] Failed to download {label}: {downloadHandle.OperationException?.Message}");
        }

        Addressables.Release(downloadHandle);
    }

    private void RegisterOrUpdateLoadingTask(float taskProgress, string taskName)
    {
        LoadingProgressManager progressManager = LoadingProgressManager.GetInstance();
        LoadingTask existingTask = progressManager.GetTask(LoadingTaskID);

        if (existingTask != null && existingTask.IsCompleted && taskProgress < 1f)
        {
            progressManager.UnregisterTask(LoadingTaskID);
            existingTask = null;
        }

        if (existingTask == null)
        {
            progressManager.RegisterTask(LoadingTaskID, taskName, LoadingTaskWeight);
        }
        else
        {
            progressManager.UpdateTaskName(LoadingTaskID, taskName);
        }

        progressManager.UpdateTaskProgress(LoadingTaskID, taskProgress);
    }

    private void CompleteLoadingTask(string taskName)
    {
        LoadingProgressManager progressManager = LoadingProgressManager.GetInstance();

        if (progressManager.GetTaskProgress(LoadingTaskID) < 0f)
        {
            progressManager.RegisterTask(LoadingTaskID, taskName, LoadingTaskWeight);
        }
        else
        {
            progressManager.UpdateTaskName(LoadingTaskID, taskName);
        }

        progressManager.CompleteTask(LoadingTaskID);
    }

    private string BuildProgressTaskName(string label)
    {
        return $"{LoadingTaskName}: {label} {progress * 100f:F0}% ({FormatBytes(downloadedBytes)} / {FormatBytes(totalDownloadBytes)})";
    }

    private string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1} KB";
        return $"{bytes / (1024f * 1024f):F1} MB";
    }
}
