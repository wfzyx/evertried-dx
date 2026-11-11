using Evertried.UI;
using Localization;
using UnityEngine;

namespace EvertriedQoL
{
    /// <summary>
    /// Adds the "Disable Focus Timer" row to the settings panel by cloning the "Controls" dropdown (the Linux
    /// scene has no such element). The patcher calls <see cref="Setup"/> at the end of SettingsPanel.Start,
    /// <see cref="Refresh"/> from SettingsPanel.Toggle and wraps SettingsPanel.Elements with <see cref="Elements"/>.
    /// </summary>
    public static class DxSettings
    {
        private const string TextKey = "ST_DisableFocus_Text";
        private const string No = "No";
        private const string Yes = "Yes";

        // Same order as LanguageType: EN, PTBR, JP, DE, ES, RU, SC, KR, FR, TC.
        private static readonly string[] Labels =
        {
            "Disable Focus Timer", "Desativar Barra de Focus", "フォーカスバー無効", "Fokus-Balken aus",
            "Barra de Focus desactivada", "шкала Фокуса отключена", "关掉专注条", "집중 바 끄다",
            "désactiver la barre de furie", "關掉專注條",
        };

        private static DropdownSettingElement row;

        public static void Setup(SettingsPanel panel, DropdownSettingElement controls, DropdownSettingElement movement)
        {
            row = null;
            if (movement == null)
            {
                return;
            }
            try
            {
                Transform parent = movement.transform.parent;
                GameObject copy = (GameObject)Object.Instantiate(movement.gameObject);
                copy.name = "FocusTimerDisabledDropdown";
                copy.transform.SetParent(parent, false);
                copy.transform.SetSiblingIndex(movement.transform.GetSiblingIndex() + 1);

                DropdownSettingElement dropdown = copy.GetComponent<DropdownSettingElement>();
                dropdown.Setup();
                dropdown.SetNames(new[] { No, Yes });
                dropdown.SetSelection(DxFocus.Disabled ? Yes : No);
                dropdown.OnSelect.AddListener(OnSelected);

                LocalizedTextBase[] labels = copy.GetComponentsInChildren<LocalizedTextBase>(true);
                if (labels.Length > 0)
                {
                    LocalizedTextData data = ScriptableObject.CreateInstance<LocalizedTextData>();
                    data.name = TextKey;
                    LocalizedTextData.TextSetting[] settings = new LocalizedTextData.TextSetting[Labels.Length];
                    for (int i = 0; i < settings.Length; i++)
                    {
                        settings[i].language = (LanguageType)i;
                        settings[i].value = Labels[i];
                    }
                    data.Setup(settings);
                    labels[0].SetData(data);
                }

                row = dropdown;
                LogLayout(parent, controls, movement, copy, labels.Length);
            }
            catch (System.Exception e)
            {
                Debug.Log("[EvertriedQoL] Could not add the Disable Focus Timer row: " + e);
            }
        }

        /// <summary>Show the saved value whenever the panel opens (the save slot may have changed since Start).</summary>
        public static void Refresh()
        {
            if (row != null)
            {
                row.SetSelection(DxFocus.Disabled ? Yes : No);
            }
        }

        /// <summary>SettingsPanel.Elements: add the row, keeping the on-screen (sibling) order.</summary>
        public static SelectableUIElement[] Elements(SelectableUIElement[] original)
        {
            if (row == null)
            {
                return original;
            }
            SelectableUIElement[] all = new SelectableUIElement[original.Length + 1];
            for (int i = 0; i < original.Length; i++)
            {
                all[i] = original[i];
            }
            all[original.Length] = row;
            for (int i = 1; i < all.Length; i++)
            {
                SelectableUIElement item = all[i];
                int key = item.transform.GetSiblingIndex();
                int j = i - 1;
                while (j >= 0 && all[j].transform.GetSiblingIndex() > key)
                {
                    all[j + 1] = all[j];
                    j--;
                }
                all[j + 1] = item;
            }
            return all;
        }

        private static void OnSelected(int index)
        {
            DxFocus.Disabled = index == 1;
            SaveHandler handler = AutomatedSingleton<SaveHandler>.Instance;
            if (handler != null)
            {
                handler.SaveObjects(false);
            }
            Debug.Log("[EvertriedQoL] Disable Focus Timer: " + DxFocus.Disabled);
        }

        private static void LogLayout(Transform parent, DropdownSettingElement controls, DropdownSettingElement movement, GameObject copy, int labelCount)
        {
            System.Text.StringBuilder text = new System.Text.StringBuilder("[EvertriedQoL] Settings rows (" + labelCount + " label components in the new row):");
            for (int i = 0; i < parent.childCount; i++)
            {
                RectTransform rect = parent.GetChild(i) as RectTransform;
                text.Append("\n  ").Append(i).Append(' ').Append(parent.GetChild(i).name);
                if (rect != null)
                {
                    text.Append(" pos=").Append(rect.anchoredPosition).Append(" size=").Append(rect.sizeDelta);
                }
            }
            text.Append("\n  parent layout group: ").Append(parent.GetComponent<UnityEngine.UI.LayoutGroup>() != null);
            Debug.Log(text.ToString());
        }
    }
}
