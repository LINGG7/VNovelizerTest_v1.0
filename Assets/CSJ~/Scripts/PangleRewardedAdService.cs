#if VN_ENABLE_PANGLE_ADS
using System;
using System.Collections;
using ByteDance.Union;
using ByteDance.Union.Mediation;
using UnityEngine;

public sealed class PangleRewardedAdService : IRewardedAdService, IRewardedAdServiceLifecycle
{
    private const string AppId = "5820404";
    private const string AppName = "\u5723\u8bfa\u6c40\u4e4b\u68a6";
    private const string RewardAdCodeId = "104041578";
    private const float RewardedAdRequestTimeoutSeconds = 5f;

    private bool isInitialized;
    private bool sdkInitFinished;
    private bool isSdkReady;
    private bool isLoading;
    private bool isShowing;
    private bool hasAdShown;
    private bool hasPendingAdRequest;
    private RewardVideoAd loadedAd;
    private Action pendingRewarded;
    private Action pendingFailed;
    private Coroutine requestTimeoutCoroutine;
    private int requestTimeoutToken;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
    private static void RegisterService()
    {
#if !UNITY_EDITOR && UNITY_ANDROID
        RewardedAdManager.GetInstance().SetService(new PangleRewardedAdService());
#endif
    }

    public void Init()
    {
        if (isInitialized)
            return;

        isInitialized = true;
        Pangle.Init(BuildSdkConfiguration());
        Pangle.Start(SdkInitCallback);
    }

    public void ShowRewardedAd(Action onRewarded, Action onFailed)
    {
        if (isLoading || isShowing || hasPendingAdRequest)
        {
            Debug.LogWarning("[PangleRewardedAdService] Rewarded ad is already loading or showing.");
            onFailed?.Invoke();
            return;
        }

        if (string.IsNullOrEmpty(RewardAdCodeId))
        {
            Debug.LogError("[PangleRewardedAdService] RewardAdCodeId is empty. Fill in the reward ad placement code id.");
            onFailed?.Invoke();
            return;
        }

        pendingRewarded = onRewarded;
        pendingFailed = onFailed;
        hasPendingAdRequest = true;
        hasAdShown = false;
        StartRequestTimeout();

        if (!sdkInitFinished)
        {
            Debug.Log("[PangleRewardedAdService] SDK is initializing, reward ad request is pending.");
            return;
        }

        if (!isSdkReady)
        {
            Debug.LogWarning("[PangleRewardedAdService] SDK is not ready.");
            FailPending();
            return;
        }

        TryLoadPendingRewardedAd();
    }

    private void SdkInitCallback(bool success, string message)
    {
        sdkInitFinished = true;
        isSdkReady = success;
        Debug.Log("CSJM_Unity sdk init finished: success: " + success + ", message: " + message);

        if (!hasPendingAdRequest)
            return;

        if (isSdkReady)
        {
            TryLoadPendingRewardedAd();
            return;
        }

        FailPending();
    }

    private SDKConfiguration BuildSdkConfiguration()
    {
        return new SDKConfiguration.Builder()
            .SetAppId(AppId)
            .SetAppName(AppName)
            .SetUseMediation(true)
            .SetDebug(false)
            .SetThemeStatus(0)
            .SetSupportMultiProcesse(false)
            .SetPrivacyConfigurationn(GetPrivacyConfiguration())
            .Build();
    }

    private PrivacyConfiguration GetPrivacyConfiguration()
    {
        return new PrivacyConfiguration
        {
            CanUseWifiState = true,
            CanUseLocation = false,
            CanReadAppList = false,
            CanUsePhoneState = false,
            CanUseWriteExternal = false,
            CanUseAndroidId = true,
            IsCanUseMessage = false
        };
    }

    private void LoadRewardedAd()
    {
        isLoading = true;
        SDK.CreateAdNative().LoadRewardVideoAd(BuildRewardAdSlot(), new RewardVideoLoadListener(this));
    }

    private void TryLoadPendingRewardedAd()
    {
        if (!hasPendingAdRequest || isLoading || isShowing)
            return;

        LoadRewardedAd();
    }

    private AdSlot BuildRewardAdSlot()
    {
        return new AdSlot.Builder()
            .SetCodeId(RewardAdCodeId)
            .SetOrientation(AdOrientation.Horizontal)
            .SetMediationAdSlot(
                new MediationAdSlot.Builder()
                    .SetMuted(false)
                    .Build()
            )
            .Build();
    }

    private void OnRewardVideoLoaded(RewardVideoAd ad)
    {
        if (!isLoading || !hasPendingAdRequest)
        {
            ad?.Dispose();
            return;
        }

        DisposeLoadedAd();
        loadedAd = ad;
    }

    private void OnRewardVideoCached(RewardVideoAd ad)
    {
        if (!isLoading)
        {
            ad?.Dispose();
            return;
        }

        if (ad != null && !ReferenceEquals(loadedAd, ad))
        {
            DisposeLoadedAd();
            loadedAd = ad;
        }

        ShowLoadedAd();
    }

    private void OnRewardLoadFailed(int code, string message)
    {
        Debug.LogError("[PangleRewardedAdService] Rewarded ad load failed: " + code + ", " + message);
        isLoading = false;
        DisposeLoadedAd();
        FailPending();
    }

    private void ShowLoadedAd()
    {
        if (!hasPendingAdRequest)
            return;

        if (loadedAd == null)
        {
            isLoading = false;
            FailPending();
            return;
        }

        isLoading = false;
        isShowing = true;
        hasAdShown = false;
        loadedAd.SetRewardAdInteractionListener(new RewardInteractionListener(this));
        loadedAd.SetAgainRewardAdInteractionListener(new RewardAgainInteractionListener(this));
        loadedAd.ShowRewardVideoAd();
    }

    private void OnAdShown()
    {
        hasAdShown = true;
        StopRequestTimeout();
    }

    private void OnRewardArrived(bool isRewardValid)
    {
        if (!isRewardValid)
            return;

        Action rewarded = pendingRewarded;
        ClearPending();
        rewarded?.Invoke();
    }

    private void OnAdClosed()
    {
        isShowing = false;
        DisposeLoadedAd();

        if (pendingRewarded != null || pendingFailed != null)
            FailPending();
    }

    private void OnAdFailedToPlay()
    {
        isShowing = false;
        DisposeLoadedAd();
        FailPending();
    }

    private void StartRequestTimeout()
    {
        StopRequestTimeout();

        int token = ++requestTimeoutToken;
        MonoManager monoManager = MonoManager.GetInstance();
        if (monoManager != null)
            requestTimeoutCoroutine = monoManager.StartCoroutine(RequestTimeoutRoutine(token));
    }

    private void StopRequestTimeout()
    {
        requestTimeoutToken++;

        if (requestTimeoutCoroutine == null)
            return;

        MonoManager monoManager = MonoManager.GetInstance();
        if (monoManager != null)
            monoManager.StopCoroutine(requestTimeoutCoroutine);

        requestTimeoutCoroutine = null;
    }

    private IEnumerator RequestTimeoutRoutine(int token)
    {
        yield return new WaitForSecondsRealtime(RewardedAdRequestTimeoutSeconds);

        if (token != requestTimeoutToken || !hasPendingAdRequest || hasAdShown)
            yield break;

        requestTimeoutCoroutine = null;
        requestTimeoutToken++;
        Debug.LogWarning("[PangleRewardedAdService] Rewarded ad request timed out.");
        isLoading = false;
        isShowing = false;
        DisposeLoadedAd();
        FailPending();
    }

    private void DisposeLoadedAd()
    {
        if (loadedAd == null)
            return;

        loadedAd.Dispose();
        loadedAd = null;
    }

    private void FailPending()
    {
        Action failed = pendingFailed;
        ClearPending();
        failed?.Invoke();
    }

    private void ClearPending()
    {
        StopRequestTimeout();
        hasPendingAdRequest = false;
        hasAdShown = false;
        pendingRewarded = null;
        pendingFailed = null;
    }

    private sealed class RewardVideoLoadListener : IRewardVideoAdListener
    {
        private readonly PangleRewardedAdService owner;

        public RewardVideoLoadListener(PangleRewardedAdService owner)
        {
            this.owner = owner;
        }

        public void OnError(int code, string message)
        {
            owner.OnRewardLoadFailed(code, message);
        }

        public void OnRewardVideoAdLoad(RewardVideoAd ad)
        {
            owner.OnRewardVideoLoaded(ad);
        }

        public void OnRewardVideoCached()
        {
            owner.OnRewardVideoCached(null);
        }

        public void OnRewardVideoCached(RewardVideoAd ad)
        {
            owner.OnRewardVideoCached(ad);
        }
    }

    private sealed class RewardInteractionListener : IRewardAdInteractionListener
    {
        private readonly PangleRewardedAdService owner;

        public RewardInteractionListener(PangleRewardedAdService owner)
        {
            this.owner = owner;
        }

        public void OnAdShow()
        {
            owner.OnAdShown();
        }

        public void OnAdVideoBarClick() { }

        public void OnAdClose()
        {
            owner.OnAdClosed();
        }

        public void OnVideoComplete() { }

        public void OnVideoSkip() { }

        public void OnVideoError()
        {
            owner.OnAdFailedToPlay();
        }

        public void OnRewardArrived(bool isRewardValid, int rewardType, IRewardBundleModel extraInfo)
        {
            string extra = extraInfo != null ? extraInfo.ToString() : string.Empty;
            Debug.Log("CSJM_Unity OnRewardArrived verify:" + isRewardValid + " rewardType:" + rewardType + " extraInfo: " + extra);
            owner.OnRewardArrived(isRewardValid);
        }
    }

    private sealed class RewardAgainInteractionListener : IRewardAdInteractionListener
    {
        private readonly PangleRewardedAdService owner;

        public RewardAgainInteractionListener(PangleRewardedAdService owner)
        {
            this.owner = owner;
        }

        public void OnAdShow()
        {
            owner.OnAdShown();
        }

        public void OnAdVideoBarClick() { }

        public void OnAdClose() { }

        public void OnVideoComplete() { }

        public void OnVideoSkip() { }

        public void OnVideoError() { }

        public void OnRewardArrived(bool isRewardValid, int rewardType, IRewardBundleModel extraInfo)
        {
            string extra = extraInfo != null ? extraInfo.ToString() : string.Empty;
            Debug.Log("CSJM_Unity OnRewardArrived verify:" + isRewardValid + " rewardType:" + rewardType + " extraInfo: " + extra);
            owner.OnRewardArrived(isRewardValid);
        }
    }
}
#endif
