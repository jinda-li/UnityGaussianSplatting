// SPDX-License-Identifier: MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Experimental;
using UnityEngine;

namespace GaussianSplatting.Editor.Utils
{
    public class FilePickerControl
    {
        const string kLastPathPref = "nesnausk.utils.FilePickerLastPath";
        static Texture2D s_FolderIcon => EditorGUIUtility.FindTexture(EditorResources.emptyFolderIconName);
        static Texture2D s_FileIcon => EditorGUIUtility.FindTexture(EditorResources.folderIconName);
        static GUIStyle s_StyleTextFieldText;
        static GUIStyle s_StyleTextFieldDropdown;
        static readonly int kPathFieldControlID = "FilePickerPathField".GetHashCode();
        const int kIconSize = 15;
        const int kRecentPathsCount = 20;

        public static string PathToDisplayString(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "<none>";
            path = path.Replace('\\', '/');
            string[] parts = path.Split('/');

            // check if filename is not some super generic one
            var baseName = Path.GetFileNameWithoutExtension(parts[^1]).ToLowerInvariant();
            if (baseName != "point_cloud" && baseName != "splat" && baseName != "input")
                return parts[^1];

            // otherwise if filename is just some generic "point cloud" type, then take some folder names above it into account
            if (parts.Length >= 4)
                path = string.Join('/', parts.TakeLast(4));

            path = path.Replace('/', '-');
            return path;
        }

        class PreviousPaths
        {
            public PreviousPaths(List<string> paths)
            {
                this.paths = paths;
                UpdateContent();
            }
            public void UpdateContent()
            {
                this.content = paths.Select(p => new GUIContent(PathToDisplayString(p))).ToArray();
            }
            public List<string> paths;
            public GUIContent[] content;
        }
        Dictionary<string, PreviousPaths> m_PreviousPaths = new();

        void PopulatePreviousPaths(string nameKey)
        {
            if (m_PreviousPaths.ContainsKey(nameKey))
                return;

            List<string> prevPaths = new();
            for (int i = 0; i < kRecentPathsCount; ++i)
            {
                string path = EditorPrefs.GetString($"{kLastPathPref}-{nameKey}-{i}");
                if (!string.IsNullOrWhiteSpace(path))
                    prevPaths.Add(path);
            }
            m_PreviousPaths.Add(nameKey, new PreviousPaths(prevPaths));
        }

        void UpdatePreviousPaths(string nameKey, string path)
        {
            if (!m_PreviousPaths.ContainsKey(nameKey))
            {
                m_PreviousPaths.Add(nameKey, new PreviousPaths(new List<string>()));
            }
            var prevPaths = m_PreviousPaths[nameKey];
            prevPaths.paths.Remove(path);
            prevPaths.paths.Insert(0, path);
            while (prevPaths.paths.Count > kRecentPathsCount)
                prevPaths.paths.RemoveAt(prevPaths.paths.Count - 1);
            prevPaths.UpdateContent();

            for (int i = 0; i < prevPaths.paths.Count; ++i)
            {
                EditorPrefs.SetString($"{kLastPathPref}-{nameKey}-{i}", prevPaths.paths[i]);
            }
        }

        static bool CheckPath(string path, bool isFolder)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            if (isFolder)
            {
                if (!Directory.Exists(path))
                    return false;
            }
            else
            {
                if (!File.Exists(path))
                    return false;
            }
            return true;
        }

        static bool IsFolderOrFilePath(string path, string fileExtension)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;
            if (Directory.Exists(path))
                return true;
            return !string.IsNullOrWhiteSpace(fileExtension) &&
                   File.Exists(path) &&
                   path.EndsWith("." + fileExtension, StringComparison.OrdinalIgnoreCase);
        }

        string m_DeferredPath;
        string m_DeferredPathKey;

        static string PathAbsToStorage(string path)
        {
            path = path.Replace('\\', '/');
            var dataPath = Application.dataPath;
            if (path.StartsWith(dataPath, StringComparison.Ordinal))
            {
                path = Path.GetRelativePath($"{dataPath}/..", path);
                path = path.Replace('\\', '/');
            }
            return path;
        }

        bool CheckAndSetNewPath(ref string path, string nameKey, bool isFolder, string optionalFileExtension = null)
        {
            path = PathAbsToStorage(path);
            bool valid = optionalFileExtension != null
                ? IsFolderOrFilePath(path, optionalFileExtension)
                : CheckPath(path, isFolder);
            if (valid)
            {
                EditorPrefs.SetString($"{kLastPathPref}-{nameKey}", path);
                UpdatePreviousPaths(nameKey, path);
                GUI.changed = true;
                Event.current.Use();
                return true;
            }
            return false;
        }

        void SetDeferredPath(string nameKey, string path)
        {
            // Use empty string as a sentinel for "cleared"; null means no deferred change.
            m_DeferredPath = path ?? "";
            m_DeferredPathKey = nameKey;
            EditorPrefs.SetString($"{kLastPathPref}-{nameKey}", m_DeferredPath);
            if (!string.IsNullOrWhiteSpace(m_DeferredPath))
                UpdatePreviousPaths(nameKey, m_DeferredPath);
        }

        bool TryConsumeDeferredPath(string nameKey, ref string value)
        {
            if (m_DeferredPath == null || m_DeferredPathKey != nameKey)
                return false;

            value = string.IsNullOrWhiteSpace(m_DeferredPath) ? null : m_DeferredPath;
            m_DeferredPath = null;
            m_DeferredPathKey = null;
            GUI.changed = true;
            return true;
        }

        string ClearPath(string nameKey)
        {
            EditorPrefs.SetString($"{kLastPathPref}-{nameKey}", "");
            GUI.changed = true;
            return null;
        }

        string LastPathHint(string nameKey, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
            return EditorPrefs.GetString($"{kLastPathPref}-{nameKey}");
        }

        string BrowsePath(string value, string extension, string nameKey, bool isFolder, bool saveDialog)
        {
            string hint = LastPathHint(nameKey, value);
            string newPath;
            string openToPath = string.Empty;
            if (isFolder)
            {
                if (Directory.Exists(hint))
                    openToPath = hint;
                newPath = EditorUtility.OpenFolderPanel("Select folder", openToPath, "");
            }
            else if (saveDialog)
            {
                string defaultName = string.Empty;
                if (!string.IsNullOrWhiteSpace(hint))
                {
                    try
                    {
                        openToPath = Path.GetDirectoryName(Path.GetFullPath(hint));
                        defaultName = Path.GetFileName(hint);
                    }
                    catch (Exception)
                    {
                        openToPath = string.Empty;
                        defaultName = string.Empty;
                    }
                }
                newPath = EditorUtility.SaveFilePanel("Save file", openToPath, defaultName, extension);
            }
            else
            {
                if (File.Exists(hint))
                    openToPath = Path.GetDirectoryName(hint);
                else if (Directory.Exists(hint))
                    openToPath = hint;
                newPath = EditorUtility.OpenFilePanel("Select file", openToPath, extension);
            }

            if (saveDialog && !isFolder)
            {
                if (string.IsNullOrWhiteSpace(newPath))
                    return value;
                newPath = PathAbsToStorage(newPath);
                EditorPrefs.SetString($"{kLastPathPref}-{nameKey}", newPath);
                UpdatePreviousPaths(nameKey, newPath);
                GUI.changed = true;
                return newPath;
            }

            if (CheckAndSetNewPath(ref newPath, nameKey, isFolder))
                return newPath;
            return value;
        }

        void ShowPathContextMenu(
            string value,
            string extension,
            string nameKey,
            bool isFolder,
            bool saveDialog,
            Action<string> setPath)
        {
            var menu = new GenericMenu();
            menu.AddItem(new GUIContent(isFolder ? "Select folder..." : (saveDialog ? "Choose save path..." : "Select file...")),
                false, () => setPath(BrowsePath(value, extension, nameKey, isFolder, saveDialog)));
            if (!string.IsNullOrWhiteSpace(value) && (File.Exists(value) || Directory.Exists(value)))
                menu.AddItem(new GUIContent("Reveal in Finder"), false, () => EditorUtility.RevealInFinder(value));
            else
                menu.AddDisabledItem(new GUIContent("Reveal in Finder"));
            if (!string.IsNullOrWhiteSpace(value))
                menu.AddItem(new GUIContent("Clear"), false, () => setPath(ClearPath(nameKey)));
            else
                menu.AddDisabledItem(new GUIContent("Clear"));
            menu.ShowAsContext();
        }

        string PreviousPathsDropdown(Rect position, string value, string nameKey, bool isFolder, string optionalFileExtension = null)
        {
            PopulatePreviousPaths(nameKey);

            // Do not auto-fill empty values from last-used prefs — that made Clear impossible to keep.
            m_PreviousPaths.TryGetValue(nameKey, out var prevPaths);

            EditorGUI.BeginDisabledGroup(prevPaths == null || prevPaths.paths.Count == 0);
            EditorGUI.BeginChangeCheck();
            int oldIndent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;
            int parameterIndex = EditorGUI.Popup(position, GUIContent.none, -1, prevPaths.content, s_StyleTextFieldDropdown);
            if (EditorGUI.EndChangeCheck() && parameterIndex < prevPaths.paths.Count)
            {
                string newValue = prevPaths.paths[parameterIndex];
                if (CheckAndSetNewPath(ref newValue, nameKey, isFolder, optionalFileExtension))
                    value = newValue;
            }
            EditorGUI.indentLevel = oldIndent;
            EditorGUI.EndDisabledGroup();
            return value;
        }

        // null extension picks folders
        public string PathFieldGUI(Rect position, GUIContent label, string value, string extension, string nameKey, bool saveDialog = false)
        {
            s_StyleTextFieldText ??= new GUIStyle("TextFieldDropDownText");
            s_StyleTextFieldDropdown ??= new GUIStyle("TextFieldDropdown");
            bool isFolder = extension == null;

            int controlId = GUIUtility.GetControlID(kPathFieldControlID, FocusType.Keyboard, position);
            Rect fullRect = EditorGUI.PrefixLabel(position, controlId, label);
            Rect textRect = new Rect(fullRect.x, fullRect.y, fullRect.width - s_StyleTextFieldDropdown.fixedWidth, fullRect.height);
            Rect dropdownRect = new Rect(textRect.xMax, fullRect.y, s_StyleTextFieldDropdown.fixedWidth, fullRect.height);
            Rect iconRect = new Rect(textRect.xMax - kIconSize, textRect.y, kIconSize, textRect.height);

            // Apply deferred path from context-menu callbacks first (IMGUI can't mutate mid-event safely otherwise).
            TryConsumeDeferredPath(nameKey, ref value);
            value = PreviousPathsDropdown(dropdownRect, value, nameKey, isFolder);

            string displayText = PathToDisplayString(value);
            string tooltip = string.IsNullOrWhiteSpace(value)
                ? "Click folder icon to browse · Right-click to clear"
                : $"{value}\nClick folder icon to change · Delete/Backspace or right-click → Clear";

            Event evt = Event.current;
            switch (evt.type)
            {
                case EventType.KeyDown:
                    if (GUIUtility.keyboardControl == controlId)
                    {
                        if (evt.keyCode is KeyCode.Backspace or KeyCode.Delete)
                        {
                            value = ClearPath(nameKey);
                            evt.Use();
                        }
                    }
                    break;
                case EventType.Repaint:
                    s_StyleTextFieldText.Draw(textRect, new GUIContent(displayText, tooltip), controlId, DragAndDrop.activeControlID == controlId);
                    GUI.DrawTexture(iconRect, isFolder ? s_FolderIcon : s_FileIcon, ScaleMode.ScaleToFit);
                    break;
                case EventType.MouseDown:
                    if (!GUI.enabled || !textRect.Contains(evt.mousePosition))
                        break;

                    if (evt.button == 1)
                    {
                        ShowPathContextMenu(value, extension, nameKey, isFolder, saveDialog,
                            path => SetDeferredPath(nameKey, path ?? ""));
                        // SetDeferredPath always stores; empty string means clear — consume as null below.
                        evt.Use();
                        break;
                    }

                    if (evt.button != 0)
                        break;

                    if (iconRect.Contains(evt.mousePosition))
                    {
                        value = BrowsePath(value, extension, nameKey, isFolder, saveDialog);
                        evt.Use();
                    }
                    else if (evt.clickCount >= 2 && (File.Exists(value) || Directory.Exists(value)))
                    {
                        EditorUtility.RevealInFinder(value);
                        evt.Use();
                    }
                    GUIUtility.keyboardControl = controlId;
                    break;
                case EventType.DragUpdated:
                case EventType.DragPerform:
                    if (textRect.Contains(evt.mousePosition) && GUI.enabled)
                    {
                        if (DragAndDrop.paths.Length > 0)
                        {
                            DragAndDrop.visualMode = DragAndDropVisualMode.Generic;
                            string path = DragAndDrop.paths[0];
                            path = PathAbsToStorage(path);
                            if (CheckPath(path, isFolder))
                            {
                                if (evt.type == EventType.DragPerform)
                                {
                                    UpdatePreviousPaths(nameKey, path);
                                    value = path;
                                    GUI.changed = true;
                                    DragAndDrop.AcceptDrag();
                                    DragAndDrop.activeControlID = 0;
                                }
                                else
                                    DragAndDrop.activeControlID = controlId;
                            }
                            else
                                DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                            evt.Use();
                        }
                    }
                    break;
                case EventType.DragExited:
                    if (GUI.enabled)
                    {
                        HandleUtility.Repaint();
                    }
                    break;
            }
            return value;
        }

        // Folder picker that also accepts a single file with the given extension (e.g. sog).
        public string FolderOrFilePathFieldGUI(
            Rect position,
            GUIContent label,
            string value,
            string fileExtension,
            string nameKey)
        {
            s_StyleTextFieldText ??= new GUIStyle("TextFieldDropDownText");
            s_StyleTextFieldDropdown ??= new GUIStyle("TextFieldDropdown");
            TryConsumeDeferredPath(nameKey, ref value);

            int controlId = GUIUtility.GetControlID(kPathFieldControlID, FocusType.Keyboard, position);
            Rect fullRect = EditorGUI.PrefixLabel(position, controlId, label);
            Rect textRect = new Rect(fullRect.x, fullRect.y, fullRect.width - s_StyleTextFieldDropdown.fixedWidth, fullRect.height);
            Rect dropdownRect = new Rect(textRect.xMax, fullRect.y, s_StyleTextFieldDropdown.fixedWidth, fullRect.height);
            Rect iconRect = new Rect(textRect.xMax - kIconSize, textRect.y, kIconSize, textRect.height);

            value = PreviousPathsDropdown(dropdownRect, value, nameKey, isFolder: true, fileExtension);

            string displayText = PathToDisplayString(value);
            bool isDirectory = Directory.Exists(value);
            string tooltip = string.IsNullOrWhiteSpace(value)
                ? "Click folder icon to browse · Right-click to clear"
                : $"{value}\nClick folder icon to change · Delete/Backspace or right-click → Clear";

            Event evt = Event.current;
            switch (evt.type)
            {
                case EventType.KeyDown:
                    if (GUIUtility.keyboardControl == controlId)
                    {
                        if (evt.keyCode is KeyCode.Backspace or KeyCode.Delete)
                        {
                            value = ClearPath(nameKey);
                            evt.Use();
                        }
                    }
                    break;
                case EventType.Repaint:
                    s_StyleTextFieldText.Draw(textRect, new GUIContent(displayText, tooltip), controlId, DragAndDrop.activeControlID == controlId);
                    GUI.DrawTexture(iconRect, isDirectory ? s_FolderIcon : s_FileIcon, ScaleMode.ScaleToFit);
                    break;
                case EventType.MouseDown:
                    if (!GUI.enabled || !textRect.Contains(evt.mousePosition))
                        break;

                    if (evt.button == 1)
                    {
                        string hint = LastPathHint(nameKey, value);
                        string openToPath = string.Empty;
                        if (Directory.Exists(hint))
                            openToPath = hint;
                        else if (File.Exists(hint))
                            openToPath = Path.GetDirectoryName(hint);

                        string folderStart = openToPath;
                        string fileStart = openToPath;
                        var menu = new GenericMenu();
                        menu.AddItem(new GUIContent("Select folder..."), false, () =>
                        {
                            string newPath = EditorUtility.OpenFolderPanel("Select folder", folderStart, "");
                            if (!string.IsNullOrWhiteSpace(newPath) && Directory.Exists(newPath))
                                SetDeferredPath(nameKey, PathAbsToStorage(newPath));
                        });
                        menu.AddItem(new GUIContent($"Select .{fileExtension} file..."), false, () =>
                        {
                            string newPath = EditorUtility.OpenFilePanel($"Select .{fileExtension} file", fileStart, fileExtension);
                            if (IsFolderOrFilePath(newPath, fileExtension))
                                SetDeferredPath(nameKey, PathAbsToStorage(newPath));
                        });
                        if (!string.IsNullOrWhiteSpace(value) && (File.Exists(value) || Directory.Exists(value)))
                            menu.AddItem(new GUIContent("Reveal in Finder"), false, () => EditorUtility.RevealInFinder(value));
                        else
                            menu.AddDisabledItem(new GUIContent("Reveal in Finder"));
                        if (!string.IsNullOrWhiteSpace(value))
                            menu.AddItem(new GUIContent("Clear"), false, () => SetDeferredPath(nameKey, ""));
                        else
                            menu.AddDisabledItem(new GUIContent("Clear"));
                        menu.ShowAsContext();
                        evt.Use();
                        break;
                    }

                    if (evt.button != 0)
                        break;

                    if (iconRect.Contains(evt.mousePosition))
                    {
                        string hint = LastPathHint(nameKey, value);
                        string openToPath = string.Empty;
                        if (Directory.Exists(hint))
                            openToPath = hint;
                        else if (File.Exists(hint))
                            openToPath = Path.GetDirectoryName(hint);

                        string folderStart = openToPath;
                        string fileStart = openToPath;
                        var menu = new GenericMenu();
                        menu.AddItem(new GUIContent("Select folder..."), false, () =>
                        {
                            string newPath = EditorUtility.OpenFolderPanel("Select folder", folderStart, "");
                            if (!string.IsNullOrWhiteSpace(newPath) && Directory.Exists(newPath))
                                SetDeferredPath(nameKey, PathAbsToStorage(newPath));
                        });
                        menu.AddItem(new GUIContent($"Select .{fileExtension} file..."), false, () =>
                        {
                            string newPath = EditorUtility.OpenFilePanel($"Select .{fileExtension} file", fileStart, fileExtension);
                            if (IsFolderOrFilePath(newPath, fileExtension))
                                SetDeferredPath(nameKey, PathAbsToStorage(newPath));
                        });
                        menu.ShowAsContext();
                        evt.Use();
                    }
                    else if (evt.clickCount >= 2 && (File.Exists(value) || Directory.Exists(value)))
                    {
                        EditorUtility.RevealInFinder(value);
                        evt.Use();
                    }
                    GUIUtility.keyboardControl = controlId;
                    break;
                case EventType.DragUpdated:
                case EventType.DragPerform:
                    if (textRect.Contains(evt.mousePosition) && GUI.enabled)
                    {
                        if (DragAndDrop.paths.Length > 0)
                        {
                            DragAndDrop.visualMode = DragAndDropVisualMode.Generic;
                            string path = PathAbsToStorage(DragAndDrop.paths[0]);
                            if (IsFolderOrFilePath(path, fileExtension))
                            {
                                if (evt.type == EventType.DragPerform)
                                {
                                    UpdatePreviousPaths(nameKey, path);
                                    value = path;
                                    GUI.changed = true;
                                    DragAndDrop.AcceptDrag();
                                    DragAndDrop.activeControlID = 0;
                                }
                                else
                                    DragAndDrop.activeControlID = controlId;
                            }
                            else
                                DragAndDrop.visualMode = DragAndDropVisualMode.Rejected;
                            evt.Use();
                        }
                    }
                    break;
                case EventType.DragExited:
                    if (GUI.enabled)
                        HandleUtility.Repaint();
                    break;
            }
            return value;
        }
    }
}
