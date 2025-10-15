﻿using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using System;
using System.Collections;
using System.Reflection;

[BepInPlugin("com.zlq.silksongfocusqol", "Silksong Focus QoL", "1.0.1")]
public class SilksongFocusQoL : BaseUnityPlugin
{
    private GameObject focusQoLBehaviourObject;

    // Configuration entries
    private ConfigEntry<bool> configEnableAutoPause;
    private ConfigEntry<bool> configEnableAutoUnpause;
    private ConfigEntry<float> configAutoUnpauseWindow;
    private ConfigEntry<bool> configEnableAutoMute;

    private void Awake()
    {
        // Bind config entries
        configEnableAutoMute = Config.Bind(
            "Settings",
            "EnableAutoMute",
            true,
            "Mute audio when window loses focus"
        );

        configEnableAutoPause = Config.Bind(
            "Settings",
            "EnableAutoPause",
            true,
            "Pause game when window loses focus"
        );

        configEnableAutoUnpause = Config.Bind(
            "Settings",
            "EnableAutoUnpause",
            true,
            "Unpause when focus returns"
        );

        configAutoUnpauseWindow = Config.Bind(
            "Settings",
            "AutoUnpauseWindow",
            3f,
            new ConfigDescription(
                "Time window to auto-unpause\n0 = always unpause when focus returns; >0 = only if refocus occurs within this many seconds",
                new AcceptableValueRange<float>(0f, 30f)
            )
        );

        // Watch for changes in the configuration
        Config.SettingChanged += OnConfigChanged;

        // Create the helper behaviour object
        focusQoLBehaviourObject = new GameObject("SilksongFocusQoLBehaviour", typeof(FocusQoLBehaviour));
        DontDestroyOnLoad(focusQoLBehaviourObject);

        // Pass config to the behaviour
        var behaviour = focusQoLBehaviourObject.GetComponent<FocusQoLBehaviour>();
        behaviour.SetConfig(configEnableAutoPause, configEnableAutoUnpause, configAutoUnpauseWindow, configEnableAutoMute);

        Logger.LogInfo("Silksong Focus QoL loaded");
    }

    private void OnConfigChanged(object sender, SettingChangedEventArgs e)
    {
        try
        {
            Logger.LogInfo($"Config updated: [{e.ChangedSetting.Definition.Section}] {e.ChangedSetting.Definition.Key}");

            var behaviour = focusQoLBehaviourObject?.GetComponent<FocusQoLBehaviour>();
            if (behaviour != null)
            {
                behaviour.SetConfig(configEnableAutoPause, configEnableAutoUnpause, configAutoUnpauseWindow, configEnableAutoMute);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error applying config change: {ex}");
        }
    }

    private void OnDestroy()
    {
        Config.SettingChanged -= OnConfigChanged;

        if (focusQoLBehaviourObject != null)
            Destroy(focusQoLBehaviourObject);
    }

    private class FocusQoLBehaviour : MonoBehaviour
    {
        private bool wasPausedByMod = false;       // True only if the mod successfully paused the game
        private bool wasAlreadyPaused = false;     // True if game was paused before we lost focus
        private float focusLostTime = 0f;          // Time when window lost focus
        private bool isTrackingTime = false;       // True while waiting to auto-unpause
        private Coroutine timeoutCoroutine;        // Tracks auto-unpause coroutine
        private bool wantsToPause = false;         // True when we want to pause the game

        private ConfigEntry<bool> enableAutoPause;
        private ConfigEntry<bool> enableAutoUnpause;
        private ConfigEntry<float> autoUnpauseWindow;
        private ConfigEntry<bool> enableAutoMute;

        private Type gameManagerType;
        private PropertyInfo instanceProp;
        private MethodInfo isGamePausedMethod;
        private MethodInfo pauseGameToggle;
        private ParameterInfo[] pauseMethodParameters;

        public void SetConfig(
            ConfigEntry<bool> enableAutoPause,
            ConfigEntry<bool> enableAutoUnpause,
            ConfigEntry<float> autoUnpauseWindow,
            ConfigEntry<bool> enableAutoMute)
        {
            this.enableAutoPause = enableAutoPause;
            this.enableAutoUnpause = enableAutoUnpause;
            this.autoUnpauseWindow = autoUnpauseWindow;
            this.enableAutoMute = enableAutoMute;

            // Prepare reflection methods for pausing/unpausing
            CacheGameManagerMethods();
        }

        private void CacheGameManagerMethods()
        {
            gameManagerType = Type.GetType("GameManager") ?? FindTypeByName("GameManager");
            if (gameManagerType != null)
            {
                instanceProp = gameManagerType.GetProperty("instance", BindingFlags.Static | BindingFlags.Public)
                              ?? gameManagerType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
                isGamePausedMethod = gameManagerType.GetMethod("IsGamePaused", BindingFlags.Instance | BindingFlags.Public);
                pauseGameToggle = gameManagerType.GetMethod("PauseGameToggle", BindingFlags.Instance | BindingFlags.Public);

                if (pauseGameToggle != null)
                    pauseMethodParameters = pauseGameToggle.GetParameters();
            }
        }

        private void Update()
        {
            // Keep trying to pause if we wanted to pause but the game wasn't ready yet
            if (!wantsToPause) return;

            if (!IsGamePaused())
            {
                AttemptToPauseGame();
            }
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (!isActiveAndEnabled) return;

            try
            {
                if (!hasFocus)
                {
                    focusLostTime = Time.unscaledTime;

                    // Check if game was already paused before we lost focus
                    wasAlreadyPaused = IsGamePaused();

                    if (enableAutoPause.Value)
                    {
                        wantsToPause = true;

                        // Only attempt to pause if the game wasn't already paused
                        if (!wasAlreadyPaused)
                        {
                            AttemptToPauseGame();
                        }
                    }

                    if (enableAutoUnpause.Value && autoUnpauseWindow.Value > 0)
                    {
                        isTrackingTime = true;
                        StopTrackingCoroutine();
                        timeoutCoroutine = StartCoroutine(TimeoutTracking());
                    }
                }
                else
                {
                    // Focus returned - stop pause attempts
                    wantsToPause = false;
                    StopTrackingCoroutine();

                    if (enableAutoUnpause.Value && wasPausedByMod)
                    {
                        bool shouldUnpause = true;
                        if (autoUnpauseWindow.Value > 0f)
                            shouldUnpause = isTrackingTime && (Time.unscaledTime - focusLostTime) <= autoUnpauseWindow.Value;

                        if (shouldUnpause)
                        {
                            AttemptToUnpauseGame();
                        }
                    }

                    // Reset tracking states
                    wasPausedByMod = false;
                    wasAlreadyPaused = false;
                    isTrackingTime = false;
                }

                if (enableAutoMute.Value)
                    AudioListener.pause = !hasFocus;
            }
            catch (Exception)
            {
                // Fail silently
            }
        }

        private void StopTrackingCoroutine()
        {
            if (timeoutCoroutine != null)
            {
                StopCoroutine(timeoutCoroutine);
                timeoutCoroutine = null;
            }
        }

        private IEnumerator TimeoutTracking()
        {
            // Wait for the auto-unpause window to expire
            yield return new WaitForSecondsRealtime(autoUnpauseWindow.Value);
            isTrackingTime = false;
            timeoutCoroutine = null;
        }

        private bool IsGamePaused()
        {
            try
            {
                if (gameManagerType == null || instanceProp == null || isGamePausedMethod == null)
                    return false;

                var instance = instanceProp.GetValue(null);
                if (instance == null) return false;

                return (bool)isGamePausedMethod.Invoke(instance, null);
            }
            catch
            {
                return false;
            }
        }

        private void AttemptToPauseGame()
        {
            try
            {
                if (gameManagerType == null || instanceProp == null || pauseGameToggle == null) return;

                var instance = instanceProp.GetValue(null);
                if (instance == null) return;

                // Only pause if the game isn't already paused
                if (!IsGamePaused())
                {
                    object enumerator = null;
                    if (pauseMethodParameters.Length == 1 && pauseMethodParameters[0].ParameterType == typeof(bool))
                        enumerator = pauseGameToggle.Invoke(instance, new object[] { true });
                    else if (pauseMethodParameters.Length == 0)
                        enumerator = pauseGameToggle.Invoke(instance, null);

                    // Start the pause coroutine
                    if (enumerator != null && enumerator is IEnumerator)
                    {
                        StartCoroutine((IEnumerator)enumerator);
                        wasPausedByMod = true; // Only set this if we actually paused the game
                    }
                }
            }
            catch
            {
                // Fail silently
            }
        }

        private void AttemptToUnpauseGame()
        {
            try
            {
                if (gameManagerType == null || instanceProp == null || pauseGameToggle == null) return;

                var instance = instanceProp.GetValue(null);
                if (instance == null) return;

                // Only unpause if currently paused AND we were the ones who paused it
                if (IsGamePaused() && wasPausedByMod)
                {
                    object enumerator = null;
                    if (pauseMethodParameters.Length == 1 && pauseMethodParameters[0].ParameterType == typeof(bool))
                        enumerator = pauseGameToggle.Invoke(instance, new object[] { false });
                    else if (pauseMethodParameters.Length == 0)
                        enumerator = pauseGameToggle.Invoke(instance, null);

                    if (enumerator != null && enumerator is IEnumerator)
                        StartCoroutine((IEnumerator)enumerator);
                }
            }
            catch
            {
                // Fail silently
            }
        }

        private Type FindTypeByName(string typeName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in assembly.GetTypes())
                    {
                        if (type.Name == typeName)
                            return type;
                    }
                }
                catch (ReflectionTypeLoadException) { continue; }
            }
            return null;
        }

        private void OnDisable()
        {
            StopTrackingCoroutine();
        }
    }
}