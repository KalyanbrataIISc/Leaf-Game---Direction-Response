using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using LeafGame;
using PaddleGame;
using UnityEngine;

namespace FeatureAttention
{
    /// <summary>Shared launcher and persistent settings surface for both experiments.</summary>
    public sealed class FeatureAttentionAppController : MonoBehaviour
    {
        enum ScreenState { Home, Settings }
        enum SettingsTab { Common, LeafGame, PaddleGame }

        [Serializable]
        sealed class CommonSettings
        {
            [Header("Application")]
            public string launcherTitle = "Feature Attention Games";
            public string launcherSubtitle = "Choose a task, calibrate the BCI, and begin the session.";
            public bool enableBciCsvLogging = true;

            [Header("Participant and Session")]
            public string participantNumber = "000";
            public string sessionNumber = "001";
        }

        sealed class SettingEntry
        {
            public object owner;
            public FieldInfo field;
            public string label;
            public string header;
            public string[] values;
            public bool multiline;
        }

        const string CommonKey = "FeatureAttention.Common.v1";
        const string LeafKey = "FeatureAttention.Leaf.v1";
        const string PaddleKey = "FeatureAttention.Paddle.v1";

        readonly CommonSettings common = new CommonSettings();
        readonly List<SettingEntry> commonEntries = new List<SettingEntry>();
        readonly List<SettingEntry> leafEntries = new List<SettingEntry>();
        readonly List<SettingEntry> paddleEntries = new List<SettingEntry>();
        readonly Vector2[] scroll = new Vector2[3];

        LeafGameController leafTemplate;
        PaddleGameController paddleTemplate;
        string defaultCommonJson, defaultLeafJson, defaultPaddleJson;
        ScreenState screen = ScreenState.Home;
        SettingsTab tab;
        string validationError = "";
        GUIStyle titleStyle, subtitleStyle, labelStyle, inputStyle, buttonStyle, tabStyle, activeTabStyle, sectionStyle, errorStyle;
        Texture2D panelTexture, inputTexture, leafButtonTexture, paddleButtonTexture, neutralButtonTexture, activeTabTexture;
        float styleScale = -1f;

        void Start()
        {
            leafTemplate = GetComponent<LeafGameController>();
            paddleTemplate = GetComponent<PaddleGameController>();
            if (leafTemplate == null || paddleTemplate == null)
            {
                validationError = "Launcher setup is incomplete: both disabled game templates are required on this object.";
                return;
            }

            leafTemplate.enabled = false;
            paddleTemplate.enabled = false;
            defaultCommonJson = JsonUtility.ToJson(common);
            defaultLeafJson = JsonUtility.ToJson(leafTemplate);
            defaultPaddleJson = JsonUtility.ToJson(paddleTemplate);
            LoadSettings();
            RebuildEntries();
        }

        void LoadSettings()
        {
            string json = PlayerPrefs.GetString(CommonKey, "");
            if (!string.IsNullOrEmpty(json)) JsonUtility.FromJsonOverwrite(json, common);
            json = PlayerPrefs.GetString(LeafKey, "");
            if (!string.IsNullOrEmpty(json)) JsonUtility.FromJsonOverwrite(json, leafTemplate);
            json = PlayerPrefs.GetString(PaddleKey, "");
            if (!string.IsNullOrEmpty(json)) JsonUtility.FromJsonOverwrite(json, paddleTemplate);
            leafTemplate.enabled = false;
            paddleTemplate.enabled = false;
        }

        void SaveSettings()
        {
            PlayerPrefs.SetString(CommonKey, JsonUtility.ToJson(common));
            PlayerPrefs.SetString(LeafKey, JsonUtility.ToJson(leafTemplate));
            PlayerPrefs.SetString(PaddleKey, JsonUtility.ToJson(paddleTemplate));
            PlayerPrefs.Save();
        }

        void RebuildEntries()
        {
            commonEntries.Clear();
            leafEntries.Clear();
            paddleEntries.Clear();
            BuildEntries(common, commonEntries, true);
            BuildEntries(leafTemplate, leafEntries, true);
            BuildEntries(paddleTemplate, paddleEntries, false);
        }

        static void BuildEntries(object owner, List<SettingEntry> result, bool hideIdentity)
        {
            FieldInfo[] fields = owner.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Array.Sort(fields, (a, b) => a.MetadataToken.CompareTo(b.MetadataToken));
            foreach (FieldInfo field in fields)
            {
                if (field.IsStatic || field.IsNotSerialized || Attribute.IsDefined(field, typeof(HideInInspector))) continue;
                bool serialized = field.IsPublic || Attribute.IsDefined(field, typeof(SerializeField));
                if (!serialized || !Supported(field.FieldType)) continue;
                if (hideIdentity && (field.Name == "participant" || field.Name == "block" || field.Name == "participantNumber" || field.Name == "sessionNumber")) continue;
                HeaderAttribute header = field.GetCustomAttribute<HeaderAttribute>();
                result.Add(new SettingEntry
                {
                    owner = owner,
                    field = field,
                    label = Nicify(field.Name),
                    header = header == null ? "" : header.header,
                    values = Format(field.FieldType, field.GetValue(owner)),
                    multiline = Attribute.IsDefined(field, typeof(TextAreaAttribute))
                });
            }
        }

        static bool Supported(Type type) => type == typeof(string) || type == typeof(bool) || type == typeof(int) ||
            type == typeof(float) || type == typeof(double) || type == typeof(Color) || type.IsEnum;

        static string[] Format(Type type, object value)
        {
            if (type == typeof(Color))
            {
                Color c = (Color)value;
                return new[] { Inv(c.r), Inv(c.g), Inv(c.b), Inv(c.a) };
            }
            if (type == typeof(float)) return new[] { Inv((float)value) };
            if (type == typeof(double)) return new[] { Inv((double)value) };
            if (type == typeof(int)) return new[] { ((int)value).ToString(CultureInfo.InvariantCulture) };
            if (type == typeof(bool)) return new[] { (bool)value ? "True" : "False" };
            return new[] { value == null ? "" : value.ToString() };
        }

        static string Inv(float value) => value.ToString("R", CultureInfo.InvariantCulture);
        static string Inv(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        static string Nicify(string value)
        {
            var result = new StringBuilder(value.Length + 12);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '_') { result.Append(' '); continue; }
                if (i > 0 && char.IsUpper(c) && (char.IsLower(value[i - 1]) || char.IsDigit(value[i - 1]))) result.Append(' ');
                result.Append(i == 0 ? char.ToUpperInvariant(c) : c);
            }
            return result.ToString();
        }

        void OnGUI()
        {
            BuildStyles();
            DrawBackground();
            Rect safe = TopLeftSafeArea();
            if (screen == ScreenState.Home) DrawHome(safe);
            else DrawSettings(safe);
        }

        void DrawHome(Rect safe)
        {
            float u = UiScale();
            float width = Mathf.Min(820 * u, safe.width * 0.90f);
            float height = Mathf.Min(650 * u, safe.height * 0.88f);
            Rect card = new Rect(safe.center.x - width * 0.5f, safe.center.y - height * 0.5f, width, height);
            GUI.Box(card, GUIContent.none, PanelStyle());
            GUI.Label(new Rect(card.x + 30 * u, card.y + 32 * u, card.width - 60 * u, 70 * u), common.launcherTitle, titleStyle);
            GUI.Label(new Rect(card.x + 44 * u, card.y + 102 * u, card.width - 88 * u, 58 * u), common.launcherSubtitle, subtitleStyle);

            float labelWidth = 250 * u;
            float rowY = card.y + 190 * u;
            DrawIdentityRow(card, rowY, labelWidth, "Participant number", ref common.participantNumber);
            rowY += 76 * u;
            DrawIdentityRow(card, rowY, labelWidth, "Session number", ref common.sessionNumber);

            float gap = 18 * u;
            float buttonWidth = (card.width - 78 * u - gap) * 0.5f;
            float buttonY = card.y + 385 * u;
            if (GUI.Button(new Rect(card.x + 30 * u, buttonY, buttonWidth, 78 * u), "Play Leaf Game", ColoredButtonStyle(leafButtonTexture))) LaunchLeaf();
            if (GUI.Button(new Rect(card.x + 48 * u + buttonWidth, buttonY, buttonWidth, 78 * u), "Play Paddle Game", ColoredButtonStyle(paddleButtonTexture))) LaunchPaddle();
            if (GUI.Button(new Rect(card.center.x - 190 * u, card.yMax - 88 * u, 380 * u, 58 * u), "Settings", buttonStyle))
            {
                validationError = "";
                screen = ScreenState.Settings;
            }

            if (!string.IsNullOrEmpty(validationError))
                GUI.Label(new Rect(card.x + 35 * u, card.yMax - 148 * u, card.width - 70 * u, 50 * u), validationError, errorStyle);
        }

        void DrawIdentityRow(Rect card, float y, float labelWidth, string label, ref string value)
        {
            float u = UiScale();
            GUI.Label(new Rect(card.x + 38 * u, y, labelWidth, 54 * u), label, labelStyle);
            value = GUI.TextField(new Rect(card.x + 52 * u + labelWidth, y, card.width - labelWidth - 90 * u, 54 * u), value, inputStyle);
        }

        void DrawSettings(Rect safe)
        {
            float u = UiScale();
            float margin = Mathf.Max(12 * u, safe.width * 0.025f);
            Rect shell = new Rect(safe.x + margin, safe.y + margin, safe.width - 2 * margin, safe.height - 2 * margin);
            GUI.Box(shell, GUIContent.none, PanelStyle());
            GUI.Label(new Rect(shell.x + 26 * u, shell.y + 12 * u, shell.width - 360 * u, 55 * u), "Settings", sectionStyle);

            if (GUI.Button(new Rect(shell.xMax - 330 * u, shell.y + 12 * u, 145 * u, 48 * u), "Reset all", buttonStyle)) ResetAll();
            if (GUI.Button(new Rect(shell.xMax - 170 * u, shell.y + 12 * u, 140 * u, 48 * u), "Save & Back", buttonStyle))
            {
                if (ApplyAll()) { SaveSettings(); screen = ScreenState.Home; }
            }

            float tabY = shell.y + 78 * u;
            float tabGap = 10 * u;
            float tabWidth = (shell.width - 52 * u - 2 * tabGap) / 3f;
            DrawTabButton(new Rect(shell.x + 26 * u, tabY, tabWidth, 48 * u), SettingsTab.Common, "Common");
            DrawTabButton(new Rect(shell.x + 26 * u + tabWidth + tabGap, tabY, tabWidth, 48 * u), SettingsTab.LeafGame, "Leaf Game");
            DrawTabButton(new Rect(shell.x + 26 * u + 2 * (tabWidth + tabGap), tabY, tabWidth, 48 * u), SettingsTab.PaddleGame, "Paddle Game");

            List<SettingEntry> entries = EntriesFor(tab);
            float viewportY = tabY + 62 * u;
            float errorHeight = string.IsNullOrEmpty(validationError) ? 0 : 46 * u;
            Rect viewport = new Rect(shell.x + 24 * u, viewportY, shell.width - 48 * u, shell.yMax - viewportY - 20 * u - errorHeight);
            float contentHeight = ContentHeight(entries, u);
            Rect content = new Rect(0, 0, Mathf.Max(420 * u, viewport.width - 26 * u), contentHeight);
            int tabIndex = (int)tab;
            scroll[tabIndex] = GUI.BeginScrollView(viewport, scroll[tabIndex], content, false, true);
            float y = 10 * u;
            foreach (SettingEntry entry in entries)
            {
                if (!string.IsNullOrEmpty(entry.header))
                {
                    GUI.Label(new Rect(8 * u, y, content.width - 16 * u, 36 * u), entry.header, sectionStyle);
                    y += 42 * u;
                }
                DrawEntry(entry, 8 * u, y, content.width - 16 * u, u);
                y += (entry.multiline ? 146 : 56) * u;
            }
            GUI.EndScrollView();
            if (!string.IsNullOrEmpty(validationError)) GUI.Label(new Rect(shell.x + 30 * u, shell.yMax - 53 * u, shell.width - 60 * u, 42 * u), validationError, errorStyle);
        }

        void DrawTabButton(Rect rect, SettingsTab value, string text)
        {
            if (GUI.Button(rect, text, tab == value ? activeTabStyle : tabStyle))
            {
                if (ApplyAll()) { validationError = ""; tab = value; }
            }
        }

        List<SettingEntry> EntriesFor(SettingsTab value) => value == SettingsTab.Common ? commonEntries : value == SettingsTab.LeafGame ? leafEntries : paddleEntries;

        static float ContentHeight(List<SettingEntry> entries, float u)
        {
            float height = 20 * u;
            foreach (SettingEntry entry in entries) height += (string.IsNullOrEmpty(entry.header) ? 0 : 42 * u) + (entry.multiline ? 146 : 56) * u;
            return height;
        }

        void DrawEntry(SettingEntry entry, float x, float y, float width, float u)
        {
            float rowHeight = (entry.multiline ? 136 : 46) * u;
            float labelWidth = Mathf.Clamp(width * 0.43f, 220 * u, 520 * u);
            float inputX = x + labelWidth + 12 * u;
            float inputWidth = Mathf.Max(120 * u, width - labelWidth - 12 * u);
            GUI.Label(new Rect(x, y, labelWidth, rowHeight), entry.label, labelStyle);
            Type type = entry.field.FieldType;
            if (type == typeof(bool))
            {
                bool current = string.Equals(entry.values[0], "True", StringComparison.OrdinalIgnoreCase);
                bool next = GUI.Toggle(new Rect(inputX, y, inputWidth, rowHeight), current, current ? "On" : "Off", buttonStyle);
                entry.values[0] = next ? "True" : "False";
            }
            else if (type.IsEnum)
            {
                if (GUI.Button(new Rect(inputX, y, inputWidth, rowHeight), entry.values[0], buttonStyle))
                {
                    string[] names = Enum.GetNames(type);
                    int index = Array.IndexOf(names, entry.values[0]);
                    entry.values[0] = names[(Math.Max(0, index) + 1) % names.Length];
                }
            }
            else if (type == typeof(Color))
            {
                float gap = 5 * u;
                float componentWidth = (inputWidth - 3 * gap) / 4f;
                for (int i = 0; i < 4; i++) entry.values[i] = GUI.TextField(new Rect(inputX + i * (componentWidth + gap), y, componentWidth, rowHeight), entry.values[i], inputStyle);
            }
            else if (entry.multiline) entry.values[0] = GUI.TextArea(new Rect(inputX, y, inputWidth, rowHeight), entry.values[0], inputStyle);
            else entry.values[0] = GUI.TextField(new Rect(inputX, y, inputWidth, rowHeight), entry.values[0], inputStyle);
        }

        bool ApplyAll()
        {
            validationError = "";
            return Apply(commonEntries) && Apply(leafEntries) && Apply(paddleEntries);
        }

        bool Apply(List<SettingEntry> entries)
        {
            var parsed = new List<object>(entries.Count);
            foreach (SettingEntry entry in entries)
            {
                if (!TryParse(entry, out object value, out validationError)) return false;
                parsed.Add(value);
            }
            for (int i = 0; i < entries.Count; i++) entries[i].field.SetValue(entries[i].owner, parsed[i]);
            return true;
        }

        static bool TryParse(SettingEntry entry, out object value, out string error)
        {
            Type type = entry.field.FieldType;
            string raw = entry.values.Length == 0 ? "" : entry.values[0];
            value = null;
            error = "";
            if (type == typeof(string)) { value = raw ?? ""; return true; }
            if (type == typeof(bool))
            {
                if (bool.TryParse(raw, out bool parsedBool)) { value = parsedBool; return true; }
                error = entry.label + " must be On or Off."; return false;
            }
            if (type.IsEnum)
            {
                try { value = Enum.Parse(type, raw, true); return true; }
                catch { error = entry.label + " has an invalid selection."; return false; }
            }
            if (type == typeof(Color))
            {
                var rgba = new float[4];
                for (int i = 0; i < 4; i++)
                {
                    if (!float.TryParse(entry.values[i], NumberStyles.Float, CultureInfo.InvariantCulture, out rgba[i])) { error = entry.label + " contains an invalid color value."; return false; }
                    rgba[i] = Mathf.Clamp01(rgba[i]);
                }
                value = new Color(rgba[0], rgba[1], rgba[2], rgba[3]); return true;
            }
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || double.IsNaN(number) || double.IsInfinity(number))
            { error = entry.label + " must be a number."; return false; }
            MinAttribute min = entry.field.GetCustomAttribute<MinAttribute>();
            RangeAttribute range = entry.field.GetCustomAttribute<RangeAttribute>();
            if (min != null) number = Math.Max(min.min, number);
            if (range != null) number = Math.Max(range.min, Math.Min(range.max, number));
            if (type == typeof(int)) { value = (int)Math.Round(number); return true; }
            if (type == typeof(float)) { value = (float)number; return true; }
            value = number; return true;
        }

        void ResetAll()
        {
            JsonUtility.FromJsonOverwrite(defaultCommonJson, common);
            JsonUtility.FromJsonOverwrite(defaultLeafJson, leafTemplate);
            JsonUtility.FromJsonOverwrite(defaultPaddleJson, paddleTemplate);
            leafTemplate.enabled = false;
            paddleTemplate.enabled = false;
            validationError = "";
            RebuildEntries();
            SaveSettings();
        }

        bool ValidateIdentity()
        {
            if (string.IsNullOrWhiteSpace(common.participantNumber)) { validationError = "Participant number is required."; return false; }
            if (string.IsNullOrWhiteSpace(common.sessionNumber)) { validationError = "Session number is required."; return false; }
            return true;
        }

        void LaunchLeaf()
        {
            if (!ValidateIdentity() || !ApplyAll()) return;
            SaveSettings();
            var gameObjectInstance = new GameObject("Leaf Game Runtime");
            gameObjectInstance.SetActive(false);
            var controller = gameObjectInstance.AddComponent<LeafGameController>();
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(leafTemplate), controller);
            controller.enabled = true;
            controller.ConfigureSharedLaunch(common.participantNumber, common.sessionNumber, common.enableBciCsvLogging, ReturnFromGame);
            enabled = false;
            gameObjectInstance.SetActive(true);
        }

        void LaunchPaddle()
        {
            if (!ValidateIdentity() || !ApplyAll()) return;
            SaveSettings();
            var gameObjectInstance = new GameObject("Paddle Game Runtime");
            gameObjectInstance.SetActive(false);
            var controller = gameObjectInstance.AddComponent<PaddleGameController>();
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(paddleTemplate), controller);
            controller.enabled = true;
            controller.ConfigureSharedLaunch(common.participantNumber, common.sessionNumber, common.enableBciCsvLogging, ReturnFromGame);
            enabled = false;
            gameObjectInstance.SetActive(true);
        }

        void ReturnFromGame()
        {
            validationError = "";
            screen = ScreenState.Home;
            enabled = true;
        }

        float UiScale() => Mathf.Clamp(Mathf.Min(Screen.width, Screen.height) / 1080f, 0.55f, 2.5f);

        Rect TopLeftSafeArea()
        {
            Rect safe = Screen.safeArea;
            return new Rect(safe.x, Screen.height - safe.y - safe.height, safe.width, safe.height);
        }

        void DrawBackground()
        {
            Color old = GUI.color;
            GUI.color = new Color32(5, 20, 32, 255);
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), Texture2D.whiteTexture);
            GUI.color = new Color(0.08f, 0.42f, 0.48f, 0.22f);
            GUI.DrawTexture(new Rect(0, Screen.height * 0.68f, Screen.width, Screen.height * 0.32f), Texture2D.whiteTexture);
            GUI.color = old;
        }

        void BuildStyles()
        {
            float u = UiScale();
            if (titleStyle != null && Mathf.Abs(styleScale - u) < 0.001f) return;
            styleScale = u;
            EnsureTextures();
            titleStyle = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(44 * u), alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = true };
            titleStyle.normal.textColor = Color.white;
            subtitleStyle = new GUIStyle(titleStyle) { fontSize = Mathf.RoundToInt(22 * u), fontStyle = FontStyle.Normal };
            subtitleStyle.normal.textColor = new Color(0.75f, 0.88f, 0.90f);
            labelStyle = new GUIStyle(subtitleStyle) { alignment = TextAnchor.MiddleLeft };
            inputStyle = new GUIStyle(GUI.skin.textField) { fontSize = Mathf.RoundToInt(25 * u), padding = new RectOffset(14, 14, 8, 8), border = new RectOffset(14, 14, 14, 14), wordWrap = true };
            inputStyle.normal.background = inputTexture;
            inputStyle.focused.background = inputTexture;
            inputStyle.normal.textColor = inputStyle.focused.textColor = new Color(0.04f, 0.10f, 0.13f);
            buttonStyle = ColoredButtonStyle(neutralButtonTexture);
            tabStyle = new GUIStyle(buttonStyle) { fontSize = Mathf.RoundToInt(22 * u) };
            activeTabStyle = new GUIStyle(tabStyle);
            activeTabStyle.normal.background = activeTabTexture;
            sectionStyle = new GUIStyle(titleStyle) { fontSize = Mathf.RoundToInt(28 * u), alignment = TextAnchor.MiddleLeft };
            errorStyle = new GUIStyle(subtitleStyle);
            errorStyle.normal.textColor = new Color(1f, 0.35f, 0.3f);
        }

        GUIStyle PanelStyle()
        {
            return new GUIStyle(GUI.skin.box) { normal = { background = panelTexture }, border = new RectOffset(18, 18, 18, 18) };
        }

        GUIStyle ColoredButtonStyle(Texture2D texture)
        {
            float u = UiScale();
            var style = new GUIStyle(GUI.skin.button) { fontSize = Mathf.RoundToInt(25 * u), fontStyle = FontStyle.Bold, border = new RectOffset(16, 16, 16, 16) };
            style.normal.background = style.hover.background = style.active.background = texture;
            style.normal.textColor = style.hover.textColor = style.active.textColor = Color.white;
            return style;
        }

        void EnsureTextures()
        {
            if (panelTexture != null) return;
            panelTexture = Solid(new Color(0.035f, 0.09f, 0.13f, 0.97f));
            inputTexture = Solid(new Color(0.94f, 0.98f, 0.98f, 1f));
            leafButtonTexture = Solid(new Color(0.17f, 0.58f, 0.32f, 1f));
            paddleButtonTexture = Solid(new Color(0.0f, 0.58f, 0.72f, 1f));
            neutralButtonTexture = Solid(new Color(0.19f, 0.28f, 0.34f, 1f));
            activeTabTexture = Solid(new Color(0.10f, 0.64f, 0.70f, 1f));
        }

        static Texture2D Solid(Color color)
        {
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texture.SetPixel(0, 0, color);
            texture.Apply(false, true);
            return texture;
        }

        void OnDestroy()
        {
            if (panelTexture != null) Destroy(panelTexture);
            if (inputTexture != null) Destroy(inputTexture);
            if (leafButtonTexture != null) Destroy(leafButtonTexture);
            if (paddleButtonTexture != null) Destroy(paddleButtonTexture);
            if (neutralButtonTexture != null) Destroy(neutralButtonTexture);
            if (activeTabTexture != null) Destroy(activeTabTexture);
        }
    }
}
