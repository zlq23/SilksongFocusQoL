using BepInEx;
using BepInEx.Configuration;
using UnityEngine;
using System;
using System.Collections;
using System.Reflection;

[BepInPlugin("com.zlq.silksongfocusqol", "Silksong Focus QoL", "1.0.0")]
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
            "Mute audio when window loses focus (default: true)"
        );

        configEnableAutoPause = Config.Bind(
            "Settings",
            "EnableAutoPause",
            true,
            "Pause game when window loses focus (default: true)"
        );

        configEnableAutoUnpause = Config.Bind(
            "Settings",
            "EnableAutoUnpause",
            true,
            "Unpause when focus returns (default: true)"
        );

        configAutoUnpauseWindow = Config.Bind(
            "Settings",
            "AutoUnpauseWindow",
            3f,
            new ConfigDescription(
                "Time window to auto-unpause (0-30, default: 3)\n0 = always unpause when focus returns; >0 = only if refocus occurs within this many seconds",
                new AcceptableValueRange<float>(0f, 30f)
            )
        );

        // Subscribe to config changes
        Config.SettingChanged += OnConfigChanged;

        // Create the behaviour object
        focusQoLBehaviourObject = new GameObject("SilksongFocusQoLBehaviour", typeof(FocusQoLBehaviour));
        DontDestroyOnLoad(focusQoLBehaviourObject);

        // Pass config to behaviour
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
                // Reapply updated config entries
                behaviour.SetConfig(configEnableAutoPause, configEnableAutoUnpause, configAutoUnpauseWindow, configEnableAutoMute);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error applying config change: {ex}");
        }
    }

    private class FocusQoLBehaviour : MonoBehaviour
    {
        private bool wasPausedByMod = false;
        private float focusLostTime = 0f;
        private bool isTrackingTime = false;

        private ConfigEntry<bool> enableAutoPause;
        private ConfigEntry<bool> enableAutoUnpause;
        private ConfigEntry<float> autoUnpauseWindow;
        private ConfigEntry<bool> enableAutoMute;

        // Cached reflection objects
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

            // Cache reflection objects
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

        private void OnApplicationFocus(bool hasFocus)
        {
            try
            {
                if (!hasFocus)
                {
                    focusLostTime = Time.unscaledTime;

                    if (enableAutoPause.Value)
                        AttemptToPauseGame();

                    if (enableAutoUnpause.Value && autoUnpauseWindow.Value > 0)
                        isTrackingTime = true;
                }
                else
                {
                    if (enableAutoUnpause.Value && wasPausedByMod)
                    {
                        bool shouldUnpause = true;
                        if (autoUnpauseWindow.Value > 0f)
                            shouldUnpause = isTrackingTime && (Time.unscaledTime - focusLostTime) <= autoUnpauseWindow.Value;

                        if (shouldUnpause)
                        {
                            AttemptToUnpauseGame();
                            wasPausedByMod = false;
                        }
                    }

                    isTrackingTime = false;
                }

                if (enableAutoMute.Value)
                    AudioListener.pause = !hasFocus;
            }
            catch (Exception)
            {
                // Silent fail
            }
        }

        private void AttemptToPauseGame()
        {
            try
            {
                if (gameManagerType == null || instanceProp == null || pauseGameToggle == null) return;

                var instance = instanceProp.GetValue(null);
                if (instance == null) return;

                bool isPaused = false;
                if (isGamePausedMethod != null)
                {
                    try { isPaused = (bool)isGamePausedMethod.Invoke(instance, null); }
                    catch { isPaused = false; }
                }

                if (!isPaused)
                {
                    object enumerator = null;
                    if (pauseMethodParameters.Length == 1 && pauseMethodParameters[0].ParameterType == typeof(bool))
                        enumerator = pauseGameToggle.Invoke(instance, new object[] { true });
                    else if (pauseMethodParameters.Length == 0)
                        enumerator = pauseGameToggle.Invoke(instance, null);

                    if (enumerator != null)
                    {
                        StartCoroutine((IEnumerator)enumerator);
                        wasPausedByMod = true;
                    }
                }
            }
            catch { }
        }

        private void AttemptToUnpauseGame()
        {
            try
            {
                if (gameManagerType == null || instanceProp == null || pauseGameToggle == null) return;

                var instance = instanceProp.GetValue(null);
                if (instance == null) return;

                bool isPaused = false;
                if (isGamePausedMethod != null)
                {
                    try { isPaused = (bool)isGamePausedMethod.Invoke(instance, null); }
                    catch { isPaused = false; }
                }

                if (isPaused)
                {
                    object enumerator = null;
                    if (pauseMethodParameters.Length == 1 && pauseMethodParameters[0].ParameterType == typeof(bool))
                        enumerator = pauseGameToggle.Invoke(instance, new object[] { false });
                    else if (pauseMethodParameters.Length == 0)
                        enumerator = pauseGameToggle.Invoke(instance, null);

                    if (enumerator != null)
                        StartCoroutine((IEnumerator)enumerator);
                }
            }
            catch { }
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
    }
}
