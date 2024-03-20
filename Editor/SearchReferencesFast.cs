// MIT License Copyright(c) 2024 Filip Slavov, https://github.com/NibbleByte/UnityAssetManagementTools

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace DevLocker.Tools.AssetManagement
{

	/// <summary>
	/// Tool for searching references FAST.
	/// Search is done by text (instead of loading all the assets) and works only with text assets set in unity.
	/// Created by Filip Slavov to serve the mighty Vesselin Jilov at Snapshot Games.
	/// </summary>
	public class SearchReferencesFast : EditorWindow
	{
		public interface IResultProcessor
		{
			public string Name => GetType().Name;
			public void ProcessResults(IEnumerable<UnityEngine.Object> objects);
		}

		private static readonly List<IResultProcessor> ResultProcessors;

		static SearchReferencesFast()
		{
			var implementingTypes =  TypeCache.GetTypesDerivedFrom<IResultProcessor>().ToList();

			ResultProcessors = new List<IResultProcessor>();

			foreach (var implType in implementingTypes) {
				var resultProcessor = (IResultProcessor) Activator.CreateInstance(implType);
				ResultProcessors.Add(resultProcessor);
			}
		}

		[MenuItem("Tools/Asset Management/Search References (FAST)", false, 61)]
		static void Init()
		{
			var window = GetWindow<SearchReferencesFast>("Search References");
			window.m_SearchFilter.SetTemplateEnabled("Scenes", true);
			window.m_SearchFilter.SetTemplateEnabled("Prefabs", true);
			window.m_SearchFilter.SetTemplateEnabled("Script Obj", true);
			window.m_SelectedResultProcessor = 0;
			window.minSize = new Vector2(300, 600f);
		}

		private void OnSelectionChange()
		{
			Repaint();
		}

		// Hidden Unity function, used to draw lock and other buttons at the top of the window.
		private void ShowButton(Rect rect)
		{
			if (GUI.Button(rect, "+", GUI.skin.label)) {
				SearchReferencesFast window = CreateInstance<SearchReferencesFast>();
				window.titleContent = titleContent;
				window.Show();

				window.m_SearchText = m_SearchText;
				window.m_SearchMainAssetOnly = m_SearchMainAssetOnly;
				window.m_TextToSearch = m_TextToSearch;

				window.m_SearchMetas = m_SearchMetas;
				window.m_SearchFilter = m_SearchFilter.Clone();

				window.m_SearchFilter.RefreshCounters();
			}
		}

		private bool m_ShowPreferences = false;
		private const string PROJECT_EXCLUDES_PATH = "ProjectSettings/SearchReferencesFast.Exclude.txt";

		private bool m_SearchText = false;
		private bool m_SearchMainAssetOnly = false;
		private string m_TextToSearch;
		private int m_SelectedResultProcessor;

		private enum SearchMetas
		{
			DontSearchMetas,
			SearchWithMetas,
			MetasOnly
		}

		private enum ResultsViewMode
		{
			SearchResults,
			CombinedFoundList,
		}

		private enum ResultsPathMode
		{
			Path,
			Name,
		}

		private bool m_FoldOutSearchCriterias = true;
		private SearchMetas m_SearchMetas = SearchMetas.SearchWithMetas;
		[SerializeField]
		private SearchAssetsFilter m_SearchFilter = new SearchAssetsFilter() { ExcludePackages = true };

		private string m_ResultsSearchEntryFilter = "";
		private string m_ResultsFoundEntryFilter = "";

		private SearchResult m_CurrentResults => 0 <= m_ResultsHistoryIndex && m_ResultsHistoryIndex < m_ResultsHistory.Count
			? m_ResultsHistory[m_ResultsHistoryIndex]
			: m_ResultsHistory.LastOrDefault()
			;

		private List<SearchResult> m_ResultsHistory = new List<SearchResult>();
		private int m_ResultsHistoryIndex = 0;

		private ResultsViewMode m_ResultsViewMode;
		private ResultsPathMode m_ResultsPathMode;

		private bool m_MoreResultsOperations = false;

		private Vector2 m_ScrollPos;

		private static readonly string[] _wellKnownBinaryFileExtensions = new string[] {
			".fbx",
			".dae",
			".mb",
			".ma",
			".max",
			".blend",
			".obj",
			".3ds",
			".dxf",

			".psd",
			".psb",
			".png",
			".bmp",
			".jpg",
			".tga",
			".tif",
			".tif",
			".iff",
			".gif",
			".dds",
			".exr",
			".pict",
		};


		private const string TOOL_HELP =
				"Search works only when assets are set in text mode.\n" +
				"It takes the GUIDs (from the meta files) and searches them in the assets as plain text, skipping any actual loading.\n" +
				"Useful when searching in scenes.\n\n" +
				"NOTE: Prefabs in scenes, that contain references to assets, do not store those references in the scene itself and won't be found, unless they are overridden.\n"
			;

		private SerializedObject m_SerializedObject;

		void OnEnable()
		{
			m_SearchFilter.RefreshCounters();

			if (File.Exists(PROJECT_EXCLUDES_PATH)) {
				m_SearchFilter.ExcludePreferences = new List<string>(File.ReadAllLines(PROJECT_EXCLUDES_PATH));
			} else {
				m_SearchFilter.ExcludePreferences = new List<string>();
			}

			if (string.IsNullOrWhiteSpace(m_SearchFilter.SearchFilter)) {
				m_SearchFilter.SetTemplateEnabled("Scenes", true);
				m_SearchFilter.SetTemplateEnabled("Prefabs", true);
				m_SearchFilter.SetTemplateEnabled("Script Obj", true);
			}

			m_SerializedObject = new SerializedObject(this);
		}

		private void OnDisable()
		{
			if (m_SerializedObject != null) {
				m_SerializedObject.Dispose();
			}
		}

		// Sometimes the bold style gets corrupted and displays just black text, for no good reason.
		// This forces the style to reload on re-creation.
		[NonSerialized] private static GUIStyle BoldedFoldoutStyle;
		[NonSerialized] private static GUIStyle CountLabelStyle;
		[NonSerialized] private static GUIStyle UrlStyle;
		[NonSerialized] private static GUIStyle SearchedUrlStyle;
		[NonSerialized] private static GUIStyle FoundedUrlStyle;
		[NonSerialized] private static GUIStyle ResultIconStyle;
		[NonSerialized] private static GUIStyle DarkerRowStyle;
		private readonly static GUIContent ResultsSearchedFilterLabel = new GUIContent("Searched Filter", "Filter out results by hiding some search entries.");
		private readonly static GUIContent ResultsFoundFfilterLabel = new GUIContent("Found Filter", "Filter out results by hiding some found entries (under each search entry).");
		private readonly static GUIContent ReplacePrefabsEntryButton = new GUIContent("Replace in scenes", "Replace this searched prefab entry with the specified replacement (on the left) in whichever scene it was found.");
		private readonly static GUIContent ReplacePrefabsAllButton = new GUIContent("Replace All Prefabs", "Replace ALL searched prefab entries with the specified replacement (if provided) in whichever scene they were found.");

		private readonly static GUIContent CorelateButton = new GUIContent("Corelate", "Add new results entry by making corelation between the current and previous results from the history. Use back '<' and forward '>' to preview the result entries.\n\nExample:\n1. Search which shaders are used in which materials\n2. Search those materials in which prefabs are used\n3. Corelate the last two searches so it displays which shaders are used in which prefabs");


		private void InitStyles()
		{
			BoldedFoldoutStyle = new GUIStyle(EditorStyles.foldout);
			BoldedFoldoutStyle.fontStyle = FontStyle.Bold;

			CountLabelStyle = new GUIStyle(EditorStyles.boldLabel);
			CountLabelStyle.alignment = TextAnchor.MiddleRight;

			UrlStyle = new GUIStyle(GUI.skin.label);
			UrlStyle.normal.textColor = EditorGUIUtility.isProSkin ? new Color(1.00f, 0.65f, 0.00f) : Color.blue;
			UrlStyle.hover.textColor = UrlStyle.normal.textColor;
			UrlStyle.active.textColor = Color.red;
			UrlStyle.wordWrap = false;

			SearchedUrlStyle = new GUIStyle(UrlStyle);
			SearchedUrlStyle.normal.textColor = EditorGUIUtility.isProSkin ? new Color(1.00f, 0.65f, 0.00f) : new Color(0.4f, 0.3f, 0.0f);
			SearchedUrlStyle.hover.textColor = SearchedUrlStyle.normal.textColor;

			FoundedUrlStyle = new GUIStyle(UrlStyle);
			FoundedUrlStyle.normal.textColor = EditorGUIUtility.isProSkin ? new Color(0.522f, 0.769f, 0.78f) : new Color(0.1f, 0.36f, 0.14f);
			FoundedUrlStyle.hover.textColor = FoundedUrlStyle.normal.textColor;
			FoundedUrlStyle.margin.left = 0;
			FoundedUrlStyle.padding.left = 0;

			ResultIconStyle = new GUIStyle(EditorStyles.label);

			DarkerRowStyle = new GUIStyle(GUI.skin.box);
			DarkerRowStyle.padding = new RectOffset();
			DarkerRowStyle.margin = new RectOffset();
			DarkerRowStyle.border = new RectOffset();
		}

		void OnGUI()
		{
			m_SerializedObject.Update();

			if (BoldedFoldoutStyle == null) {
				InitStyles();
			}

			if (m_ShowPreferences) {
				DrawPreferences();
				m_SerializedObject.ApplyModifiedProperties();
				return;
			}


			EditorGUILayout.Space();

			EditorGUILayout.BeginHorizontal();
			m_SearchText = EditorGUILayout.Toggle("Search Text", m_SearchText, GUILayout.ExpandWidth(false));

			if (!m_SearchText) {
				GUILayout.FlexibleSpace();
				var label = new GUIContent("Main Asset GUID Only", "If enabled search will match just the asset GUID instead of GUID + LocalId (used for sub assets).");
				m_SearchMainAssetOnly = EditorGUILayout.Toggle(label, m_SearchMainAssetOnly);
			}
			EditorGUILayout.EndHorizontal();

			if (m_SearchText) {
				m_TextToSearch = EditorGUILayout.TextField("Text", m_TextToSearch);
			} else {

				EditorGUI.BeginDisabledGroup(true);
				if (Selection.objects.Length <= 1) {
					EditorGUILayout.TextField("Selected Object", Selection.activeObject ? Selection.activeObject.name : "null");
				} else {
					EditorGUILayout.TextField("Selected Object", $"{Selection.objects.Length} Objects");
				}
				EditorGUI.EndDisabledGroup();
			}



			m_FoldOutSearchCriterias = EditorGUILayout.Foldout(m_FoldOutSearchCriterias, "Search in:", toggleOnLabelClick: true, BoldedFoldoutStyle);

			if (m_FoldOutSearchCriterias) {
				EditorGUILayout.BeginVertical(EditorStyles.helpBox);

				m_SearchFilter.DrawIncludeExcludeFolders();


				EditorGUILayout.Space();
				EditorGUILayout.BeginHorizontal();
				m_SearchMetas = (SearchMetas)EditorGUILayout.EnumPopup("Metas", m_SearchMetas);
				EditorGUILayout.EndHorizontal();
				EditorGUILayout.Space();

				m_SearchFilter.DrawTypeFilters(position.width);

				EditorGUILayout.EndVertical();
			}

			EditorGUILayout.BeginHorizontal();

			EditorGUI.BeginDisabledGroup(string.IsNullOrWhiteSpace(m_SearchFilter.SearchFilter));
			if (GUILayout.Button("Search In Project")) {

				m_ResultsSearchEntryFilter = string.Empty;
				m_ResultsFoundEntryFilter = string.Empty;

				if (m_SearchText) {
					if (string.IsNullOrWhiteSpace(m_TextToSearch)) {
						EditorUtility.DisplayDialog("Invalid Input", "Please enter some valid text to search for.", "Ok");
						GUIUtility.ExitGUI();
					}

					PerformTextSearch(m_TextToSearch);
					GUIUtility.ExitGUI();

				} else {
					if (Selection.objects.Length == 0) {
						EditorUtility.DisplayDialog("Invalid Input", "Please select some assets to search for.", "Ok");
						GUIUtility.ExitGUI();
					}

					PerformSearch(Selection.objects);
					GUIUtility.ExitGUI(); // HACK: causes Null exception in editor layout system for some reason.
				}

			}

			EditorGUI.EndDisabledGroup();

			if (GUILayout.Button("P", GUILayout.Width(20.0f))) {
				m_ShowPreferences = true;
				GUIUtility.ExitGUI();
			}

			if (GUILayout.Button("?", GUILayout.Width(20.0f))) {
				EditorUtility.DisplayDialog("Help", TOOL_HELP, "Ok");
			}

			EditorGUILayout.EndHorizontal();

			GUILayout.Space(4f);

			if (m_CurrentResults != null) {
				DrawResults();
			}

			m_SerializedObject.ApplyModifiedProperties();
		}

		private void PerformSearch(Object[] targets)
		{
			// Collect all objects guids.
			var targetEntries = new List<SearchEntryData>(targets.Length);
			for (int i = 0; i < targets.Length; ++i) {
				var target = targets[i];
				var targetPath = AssetDatabase.GetAssetPath(target);

				// If object is invalid for some reason - skip. (script of scriptable object was deleted or something)
				if (target == null) {
					Debug.LogWarning("Selected object was invalid!", target);
					continue;
				}

				if (string.IsNullOrEmpty(targetPath)) {

					// If object is prefab placed in the current scene...
#if UNITY_2018_2_OR_NEWER
					var prefab = PrefabUtility.GetCorrespondingObjectFromSource(target);
#else
					var prefab = PrefabUtility.GetPrefabParent(target);
#endif
					if (prefab) {
						target = prefab;
						targetPath = AssetDatabase.GetAssetPath(target);
					} else {
						continue;
					}
				}

				// Folder (probably).
				if (target is DefaultAsset && Directory.Exists(targetPath)) {
					if (!EditorUtility.DisplayDialog("Folder selected", $"Folder '{targetPath}' was selected as target. Do you want to target all the assets inside (recursively)?\nThe more assets you target, the slower the search will be!", "Do it!", "Skip"))
						continue;

					var guids = AssetDatabase.FindAssets("", new string[] { targetPath });
					foreach (var guid in guids) {
						var foundTarget = AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(guid));

						targetEntries.Add(new SearchEntryData(foundTarget));
					}
					continue;
				}

				targetEntries.Add(new SearchEntryData(target));
			}


			if (targetEntries.Count == 0)
				return;

			PerformSearchWork(m_SearchMetas, targetEntries, m_SearchMainAssetOnly, m_SearchFilter);
		}

		private void PerformSearchWork(SearchMetas searchMetas, List<SearchEntryData> targetEntries, bool searchMainAssetOnly, SearchAssetsFilter searchFilter)
		{
			List<string> searchPaths = searchFilter.GetFilteredPaths().ToList();

			switch (searchMetas) {
				case SearchMetas.MetasOnly:
					searchPaths = searchPaths.Select(p => p + ".meta").ToList();
					break;
				case SearchMetas.SearchWithMetas:
					searchPaths = searchPaths.SelectMany(p => new string[] { p, p + ".meta"}).ToList();
					break;
				case SearchMetas.DontSearchMetas:
					// Do nothing, data is already available.
					break;
				default: throw new NotImplementedException(searchMetas.ToString());
			}

			// Exclude well-known binary files that we never want to actually read.
			searchPaths.RemoveAll(p => _wellKnownBinaryFileExtensions.Any(ext => p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)));


			// Search targets must be provided when adding to history, to clear duplicates.
			AddNewResultsEntry(new SearchResult() {
				SearchMetas = searchMetas,
				SearchMainAssetOnly = searchMainAssetOnly,
				SearchTargetEntries = targetEntries.ToArray(),
				SearchFilter = searchFilter.Clone(),
			});

			// This used to be on demand, but having empty search results is more helpful, then having them missing.
			foreach (var target in targetEntries) {
				m_CurrentResults.Add(target.Target, new SearchResultData() { Root = target.Target });
			}

			var appDataPath = Application.dataPath;
			var allMatches = new Dictionary<SearchEntryData, List<string>>();
			var tasks = new List<Task<Dictionary<SearchEntryData, List<string>>>>();
			int threadsCount = Environment.ProcessorCount;
			var batchSize = searchPaths.Count <= threadsCount ? 1 : Mathf.CeilToInt((float)searchPaths.Count / threadsCount);
			var progressHandles = new List<ProgressHandle>();

			foreach (var pathsBatch in Split(searchPaths, batchSize)) {
				var progressHandle = new ProgressHandle(pathsBatch.Length);

				var task = new Task<Dictionary<SearchEntryData, List<string>>>(
					() => SearchJob(pathsBatch, searchMainAssetOnly, appDataPath, targetEntries, progressHandle)
				);

				tasks.Add(task);
				progressHandles.Add(progressHandle);
				// t.RunSynchronously();
				task.Start();
			}

			while (tasks.Any(a => !a.IsCompleted)) {
				ShowTasksProgress(progressHandles, searchPaths.Count, tasks.Count);
				System.Threading.Thread.Sleep(200);
			}

			foreach (var task in tasks) {
				ShowTasksProgress(progressHandles, searchPaths.Count, tasks.Count);

				foreach (var pair in task.Result) {
					SearchEntryData searchEntry = pair.Key;
					List<string> paths;

					if (!allMatches.TryGetValue(searchEntry, out paths)) {
						paths = new List<string>();
						allMatches[searchEntry] = paths;
					}

					paths.AddRange(pair.Value);
				}
			}

			EditorUtility.DisplayProgressBar("Search References FAST", "Reducing results...", 1);

			// Reduce matches
			foreach (var pair in allMatches) {
				SearchEntryData searchEntry = pair.Key;

				foreach (string matchPath in pair.Value) {

					const string metaExt = ".meta";
					bool isMeta = matchPath.EndsWith(metaExt);
					string assetPath = isMeta ? matchPath.Remove(matchPath.Length - metaExt.Length, metaExt.Length) : matchPath;

					if (assetPath == searchEntry.Target.AssetPath)
						continue;

					var foundObj = AssetDatabase.LoadMainAssetAtPath(assetPath);

					// If object is invalid for some reason - skip. (script of scriptable object was deleted or something)
					if (foundObj == null) {
						continue;
					}

					SearchResultData data = m_CurrentResults[searchEntry.Target];
					var foundLocation = new AssetHandle(foundObj);
					foundLocation.AssetPath = matchPath;

					if (!data.Found.Contains(foundLocation)) {
						data.Found.Add(foundLocation);
						m_CurrentResults.TryAddType(foundObj.GetType());
						m_CurrentResults.AddToCombinedList(foundLocation, data.Root);
					}
				}
			}

			foreach (var data in m_CurrentResults.SearchResults) {
				data.Found.Sort((l, r) => String.Compare(l.AssetPath, r.AssetPath, StringComparison.Ordinal));
			}

			foreach (var data in m_CurrentResults.CombinedFoundList) {
				data.Found.Sort((l, r) => String.Compare(l.AssetPath, r.AssetPath, StringComparison.Ordinal));
				data.ShowDetails = false;
			}

			EditorUtility.ClearProgressBar();
		}

		private static Dictionary<SearchEntryData, List<string>> SearchJob(string[] searchPaths, bool searchMainAssetOnly, string appDataPath, List<SearchEntryData> targetEntries, ProgressHandle progress)
		{
			var matches = new Dictionary<SearchEntryData, List<string>>();
			var buffers = new FileBuffers();

			for (int searchIndex = 0; searchIndex < searchPaths.Length; ++searchIndex) {
				var searchPath = searchPaths[searchIndex];
				var searchFullPath = $"{appDataPath}{searchPath.Remove(0, "Assets".Length)}";

				progress.ItemsDone = searchIndex;
				progress.LastProcessedPath = Path.GetFileName(searchPath);

				// Probably a folder. Skip it.
				if (string.IsNullOrEmpty(Path.GetExtension(searchPath))) {
					continue;
				}

				if (progress.CancelRequested) {
					break;
				}

				buffers.Clear();

				buffers.AppendFile(searchFullPath);

				string contents = buffers.StringBuilder.ToString();

				if (string.IsNullOrWhiteSpace(contents))
					continue;

				foreach (SearchEntryData searchData in targetEntries) {
					bool matchFound = ContentMatchesSearch(searchData, contents, searchPath, searchMainAssetOnly);

					if (matchFound) {
						List<string> matchedPaths;

						if (!matches.TryGetValue(searchData, out matchedPaths)) {
							matchedPaths = new List<string>();
							matches[searchData] = matchedPaths;
						}

						matchedPaths.Add(searchPath);
					}
				}
			}

			progress.ItemsDone = progress.ItemsTotal;

			return matches;
		}

		private static void ShowTasksProgress(List<ProgressHandle> progressHandles, int searchPathsCount, int tasksCount)
		{
			float progress = progressHandles.Average(ph => ph.Progress01);
			string progressDisplay = string.Join(" | ", progressHandles.Where(ph => !ph.Finished).Select(ph => $"{ph.LastProcessedPath}"));
			//string progressDisplay = string.Join(" ", progressHandles.Where(ph => !ph.Finished).Select(ph => $"[{ph.ProgressPercentage:0}%]"));
			bool cancel = EditorUtility.DisplayCancelableProgressBar($"Searching through {searchPathsCount} assets using {tasksCount} threads...", progressDisplay, progress);
			if (cancel) {
				foreach (ProgressHandle progressHandle in progressHandles) {
					progressHandle.CancelRequested = true;
				}
			}
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static bool ContentMatchesSearch(SearchEntryData searchData, string content, string searchPath, bool matchGuidOnly)
		{
			// Search by text, not object.
			if (!string.IsNullOrEmpty(searchData.TargetText))
				return content.Contains(searchData.TargetText);

			// Embedded asset searching for references in the same main asset file.
			if (searchData.Target.IsSubAsset && searchData.Target.AssetPath == searchPath) {
				return content.Contains($"{{fileID: {searchData.Target.LocalId}}}");   // If reference in the same file, guid is not used.

			} else {

				if (matchGuidOnly || string.IsNullOrEmpty(searchData.Target.LocalId))
					return content.Contains(searchData.Target.Guid);

				int guidIndex = 0;
				while (true) {

					guidIndex = content.IndexOf(searchData.Target.Guid, guidIndex + 1, StringComparison.Ordinal);

					if (guidIndex < 0)
						return false;

					int startOfLineIndex = content.LastIndexOf('\n', guidIndex);
					if (startOfLineIndex < 0) startOfLineIndex = 0;

					// Local id is to the left of the guid. Example:
					// - target: {fileID: 6986883487782155098, guid: af7e5b759d61c1b4fbf64e33d8f248dc, type: 3}
					int localIdIndex = content.IndexOf(searchData.Target.LocalId, startOfLineIndex, StringComparison.Ordinal);
					if (localIdIndex < 0)
						return false;

					int endOfLineIndex = content.IndexOf('\n', guidIndex);
					if (endOfLineIndex < 0) endOfLineIndex = content.Length;

					if (startOfLineIndex <= localIdIndex && localIdIndex < endOfLineIndex)
						return true;
				}
			}
		}

		public static bool PerformSingleSearch(Object asset, string searchPath)
		{
			var searchData = new SearchEntryData(asset);

			return ContentMatchesSearch(searchData, File.ReadAllText(searchPath), searchPath, false);
		}

		public static bool PerformSingleSearch(IEnumerable<Object> assets, string searchPath)
		{
			var searchDatas = assets.Select(a => new SearchEntryData(a)).ToList();
			foreach (SearchEntryData searchData in searchDatas) {
				if (ContentMatchesSearch(searchData, File.ReadAllText(searchPath), searchPath, false)) {
					return true;
				}
			}

			return false;
		}

		private void PerformTextSearch(string text)
		{
			PerformSearchWork(m_SearchMetas, new List<SearchEntryData>() { new SearchEntryData(text) }, m_SearchMainAssetOnly, m_SearchFilter);
			return;
		}

		/// <summary>
		/// Split input collection into chunks of a given size
		/// </summary>
		private static List<T[]> Split<T>(IList<T> targets, int chunkSize)
		{
			var output = new List<T[]>();

			var chunksCount = Mathf.FloorToInt((float)targets.Count / chunkSize);

			for (int i = 0; i < chunksCount; i++) {
				// full chunk
				var chunk = new T[chunkSize];
				for (int chunkIndex = 0; chunkIndex < chunk.Length; chunkIndex++) {
					chunk[chunkIndex] = targets[i * chunkSize + chunkIndex];
				}

				output.Add(chunk);
			}

			{
				// remaining chunk
				var remain = targets.Count % chunkSize;
				if (remain != 0) {
					var chunk = new T[remain];

					for (int j = 0; j < chunk.Length; j++) {
						chunk[j] = targets[chunksCount * chunkSize + j];
					}

					output.Add(chunk);
				}
			}
			return output;
		}

		private void DrawResults()
		{
			EditorGUILayout.BeginHorizontal();
			{
				GUILayout.Label("", GUI.skin.horizontalSlider);

				GUILayout.Label("Results", EditorStyles.boldLabel, GUILayout.ExpandWidth(false));

				GUILayout.Label("", GUI.skin.horizontalSlider);

				if (GUILayout.Button(new GUIContent("X", "Clear all results"), EditorStyles.label, GUILayout.ExpandWidth(false))) {
					m_ResultsHistory.Clear();
					GUIUtility.ExitGUI();
				}
			}
			EditorGUILayout.EndHorizontal();

			GUILayout.Space(4f);

			EditorGUILayout.BeginHorizontal();
			{
				EditorGUILayout.LabelField("Saved Search Results", GUILayout.Width(EditorGUIUtility.labelWidth - 2f));
				DrawSaveResultsSlots();
			}
			EditorGUILayout.EndHorizontal();

			EditorGUILayout.BeginHorizontal();
			{
				m_ResultsViewMode = (ResultsViewMode)EditorGUILayout.EnumPopup("Display Modes", m_ResultsViewMode, GUILayout.ExpandWidth(true));
				m_ResultsPathMode = (ResultsPathMode)EditorGUILayout.EnumPopup(m_ResultsPathMode, GUILayout.MaxWidth(75f));
			}
			EditorGUILayout.EndHorizontal();

			m_ResultsSearchEntryFilter = EditorGUILayout.TextField(ResultsSearchedFilterLabel, m_ResultsSearchEntryFilter);
			m_ResultsFoundEntryFilter = EditorGUILayout.TextField(ResultsFoundFfilterLabel, m_ResultsFoundEntryFilter);

			EditorGUILayout.BeginHorizontal();
			{
				EditorGUI.BeginDisabledGroup(m_ResultsHistoryIndex <= 0);
				if (GUILayout.Button("<", EditorStyles.miniButtonLeft, GUILayout.MaxWidth(20f))) {
					SelectPreviousResults();
				}
				EditorGUI.EndDisabledGroup();

				EditorGUI.BeginDisabledGroup(m_ResultsHistory.Count == 0);
				if (GUILayout.Button(new GUIContent("X", "Remove displayed results from history"), EditorStyles.miniButtonMid, GUILayout.MaxWidth(13f))) {
					if (EditorUtility.DisplayDialog("Remove Results?", "Are you sure you want to remove currently displayed results from the history?", "Yes", "No")) {
						m_ResultsHistory.RemoveAt(m_ResultsHistoryIndex);
						m_ResultsHistoryIndex = Mathf.Clamp(m_ResultsHistoryIndex, 0, m_ResultsHistory.Count - 1);
					}
				}
				EditorGUI.EndDisabledGroup();

				EditorGUI.BeginDisabledGroup(m_ResultsHistoryIndex >= m_ResultsHistory.Count - 1);
				if (GUILayout.Button(">", EditorStyles.miniButtonRight, GUILayout.MaxWidth(20f))) {
					SelectNextResults();
				}
				EditorGUI.EndDisabledGroup();

				if (GUILayout.Button(new GUIContent("Toggle", "Toggle collaps or expand of all the results."), EditorStyles.miniButton, GUILayout.ExpandWidth(false)) && m_CurrentResults != null) {
					List<SearchResultData> data = m_ResultsViewMode switch {
						ResultsViewMode.SearchResults => data = m_CurrentResults.SearchResults,
						ResultsViewMode.CombinedFoundList => data = m_CurrentResults.CombinedFoundList,
						_ => throw new NotImplementedException(),
					};

					if (data.Count > 0) {
						var toggledShowDetails = !data[0].ShowDetails;
						data.ForEach(data => data.ShowDetails = toggledShowDetails);
					}
				}

				EditorGUI.BeginDisabledGroup(m_CurrentResults == null || m_CurrentResults.SearchTargetEntries.Length == 0);
				if (GUILayout.Button(new GUIContent("Retry Search", "Retry same search that results were produced from"), EditorStyles.miniButton, GUILayout.ExpandWidth(false))) {
					PerformSearchWork(m_CurrentResults.SearchMetas, m_CurrentResults.SearchTargetEntries.ToList(), m_CurrentResults.SearchMainAssetOnly, m_CurrentResults.SearchFilter);
				}
				EditorGUI.EndDisabledGroup();

				Color prevBackgroundColor = GUI.backgroundColor;
				GUI.backgroundColor = m_MoreResultsOperations ? Color.green : GUI.backgroundColor;

				if (GUILayout.Button("More...", EditorStyles.miniButton)) {
					m_MoreResultsOperations = !m_MoreResultsOperations;
				}

				GUI.backgroundColor = prevBackgroundColor;

				GUILayout.FlexibleSpace();


				var selectList = new List<string>();
				selectList.Add("Select From Results...");
				selectList.AddRange(m_CurrentResults?.ResultTypesNames.Select(typeName => $"Found {typeName} Assets") ?? Enumerable.Empty<string>());
				selectList.Add("All Found");
				selectList.Add("Initial Searched Assets");

				var index = EditorGUILayout.Popup(0, selectList.ToArray());

				if (m_CurrentResults != null) {

					if (index == selectList.Count - 1) { // Appended "Initial Searched Assets"
						Selection.objects = m_CurrentResults.SearchResults.Select(data => data.Root.ToUnityObject()).ToArray();

					} else if (index == selectList.Count - 2) { // Appended "All"
						Selection.objects = m_CurrentResults.SearchResults.SelectMany(data => data.Found.Select(f => f.ToUnityObject())).Distinct().ToArray();

					} else if (m_CurrentResults.ResultTypesNames.Count > 0 && index >= 1) {
						index--;    // Exclude prepended string.
						var selectedType = m_CurrentResults.ResultTypesNames[index];

						Selection.objects = m_CurrentResults.SearchResults
							.SelectMany(pair => pair.Found.Select(f => f.ToUnityObject()))
							.Where(obj => obj.GetType().Name == selectedType)
							.ToArray();
					}
				}
			}
			EditorGUILayout.EndHorizontal();

			if (m_MoreResultsOperations) {
				DrawMoreResultsOperation();
			}

			EditorGUILayout.BeginVertical();
			m_ScrollPos = EditorGUILayout.BeginScrollView(m_ScrollPos, false, false);

			if (m_CurrentResults != null) {
				switch (m_ResultsViewMode) {
					case ResultsViewMode.SearchResults: DrawResultsData(m_CurrentResults.SearchResults, m_CurrentResults.CombinedFoundList, m_ResultsSearchEntryFilter, m_ResultsFoundEntryFilter, SearchedUrlStyle, FoundedUrlStyle, m_ResultsPathMode, showRootIcons: false, showReplaceTool: true); break;
					case ResultsViewMode.CombinedFoundList: DrawResultsData(m_CurrentResults.CombinedFoundList, m_CurrentResults.SearchResults, m_ResultsFoundEntryFilter, m_ResultsSearchEntryFilter, FoundedUrlStyle, SearchedUrlStyle, m_ResultsPathMode, showRootIcons: true, showReplaceTool: false); break;
				}
			}

			EditorGUILayout.EndScrollView();
			EditorGUILayout.EndVertical();
		}

		private static void DrawResultsData(List<SearchResultData> results, List<SearchResultData> otherList, string searchEntryFilter, string foundEntryFilter, GUIStyle rootsStyle, GUIStyle foundStyle, ResultsPathMode pathMode, bool showRootIcons, bool showReplaceTool)
		{
			const string missingLabel = "-- Missing --";

			for (int resultIndex = 0; resultIndex < results.Count; ++resultIndex) {
				var data = results[resultIndex];

				if (!string.IsNullOrEmpty(searchEntryFilter) && (data.Root.Name.IndexOf(searchEntryFilter, StringComparison.OrdinalIgnoreCase) == -1))
					continue;

				EditorGUILayout.BeginHorizontal();

				var foldOutRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight, GUILayout.Width(12f));
				data.ShowDetails = EditorGUI.Foldout(foldOutRect, data.ShowDetails, "");

				string searchPath = data.Root.AssetPath;
				if (string.IsNullOrEmpty(searchPath)) {
					searchPath = missingLabel;
				} else if (data.Root.IsSubAsset) {
					searchPath += "/" + data.Root.Name;
				}

				if (showRootIcons) {
					GUIContent icon = searchPath.EndsWith(".meta") ? EditorGUIUtility.IconContent("MetaFile Icon") : new GUIContent(AssetDatabase.GetCachedIcon(searchPath));
					if (GUILayout.Button(icon, ResultIconStyle, GUILayout.Width(EditorGUIUtility.singleLineHeight), GUILayout.Height(EditorGUIUtility.singleLineHeight)) && data.Root.Exists()) {
						EditorGUIUtility.PingObject(data.Root.ToUnityObject());
					}
					EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
				}

				if (pathMode == ResultsPathMode.Name) {
					searchPath = Path.GetFileName(searchPath);
				}

				if (GUILayout.Button(searchPath, rootsStyle, GUILayout.ExpandWidth(true)) && data.Root.Exists()) {
					EditorGUIUtility.PingObject(data.Root.ToUnityObject());
				}
				EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);

				EditorGUILayout.LabelField(data.Found.Count.ToString(), CountLabelStyle, GUILayout.MinWidth(20f));

				if (GUILayout.Button(new GUIContent("X", "Remove entry from list"), GUILayout.Width(20.0f), GUILayout.Height(16.0f))) {
					results.RemoveAt(resultIndex);
					--resultIndex;

					foreach(var otherData in otherList) {
						otherData.Found.Remove(data.Root);
					}
					otherList.RemoveAll(otherData => otherData.Found.Count == 0);
				}

				EditorGUILayout.EndHorizontal();


				if (data.ShowDetails) {
					for (int i = 0; i < data.Found.Count; ++i) {
						var found = data.Found[i];

						if (!string.IsNullOrEmpty(foundEntryFilter) && (found.Name.IndexOf(foundEntryFilter, StringComparison.OrdinalIgnoreCase) == -1))
							continue;

						if ((i + 1) % 2 == 0) {
							EditorGUILayout.BeginHorizontal(DarkerRowStyle);
						} else {
							EditorGUILayout.BeginHorizontal();
						}

						GUILayout.Space(18f);

						string foundPath = found.AssetPath;
						if (string.IsNullOrEmpty(foundPath)) {
							foundPath = missingLabel;
						} else if (found.IsSubAsset) {
							foundPath += "/" + found.Name;
						}

						if (!showRootIcons) {
							GUIContent icon = foundPath.EndsWith(".meta") ? EditorGUIUtility.IconContent("MetaFile Icon") : new GUIContent(AssetDatabase.GetCachedIcon(foundPath));
							if (GUILayout.Button(icon, ResultIconStyle, GUILayout.Width(EditorGUIUtility.singleLineHeight), GUILayout.Height(EditorGUIUtility.singleLineHeight)) && found.Exists()) {
								EditorGUIUtility.PingObject(found.ToUnityObject());
							}
							EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);
						}

						if (pathMode == ResultsPathMode.Name) {
							foundPath = Path.GetFileName(foundPath);
						}

						if (GUILayout.Button(foundPath, foundStyle, GUILayout.ExpandWidth(true)) && found.Exists()) {
							EditorGUIUtility.PingObject(found.ToUnityObject());
						}
						EditorGUIUtility.AddCursorRect(GUILayoutUtility.GetLastRect(), MouseCursor.Link);

						if (GUILayout.Button(new GUIContent("X", "Remove entry from list"), GUILayout.Width(20.0f), GUILayout.Height(14.0f))) {
							data.Found.RemoveAt(i);
							--i;

							foreach(var otherData in otherList) {
								if (otherData.Root.Equals(found)) {
									otherData.Found.Remove(data.Root);
								}
							}

							if (!results.Any(d => d.Found.Contains(found))) {
								otherList.RemoveAll(otherData => otherData.Root.Equals(found));
							}

							if (data.Found.Count == 0) {
								results.RemoveAt(resultIndex);
								--resultIndex;
							}
						}

						EditorGUILayout.EndHorizontal();
					}
				}

				if (showReplaceTool) {
					DrawReplaceSinglePrefabs(data);
				}

			}
		}


		private static void DrawReplaceSinglePrefabs(SearchResultData data)
		{
			bool showReplaceButton = data.ShowDetails
									 && data.Root.IsPrefab
									 && data.Found.Any(ah => ah.IsScene);


			if (showReplaceButton) {

				GUILayout.Space(8);

				EditorGUILayout.BeginHorizontal();
				GUILayout.Space(16 + 2);

				data.ReplacePrefab = (GameObject)EditorGUILayout.ObjectField(data.ReplacePrefab, typeof(GameObject), false);
				EditorGUILayout.LabelField(">>", GUILayout.Width(22f));

				if (GUILayout.Button(ReplacePrefabsEntryButton, GUILayout.ExpandWidth(false))) {
					if (data.ReplacePrefab == null) {
						if (!EditorUtility.DisplayDialog(
							"Delete Prefab Instances",
							"Delete all instances of the prefab in the scenes in the list?",
							"Yes", "No")) {
							GUIUtility.ExitGUI(); ;
						}
					}

					if (!data.Root.Exists())
						GUIUtility.ExitGUI();

					if (data.ReplacePrefab == data.Root.ToUnityObject()) {
						if (!EditorUtility.DisplayDialog("Wut?!", "This is the same prefab! Are you sure?", "Do it!", "Abort"))
							GUIUtility.ExitGUI();
					}

					bool reparentChildren = false;

					if (data.ReplacePrefab != null) {
						var option = EditorUtility.DisplayDialogComplex("Reparent objects?",
							"If prefab has other game objects attached to its children, what should I do with them?",
							"Re-parent",
							"Destroy",
							"Cancel");

						if (option == 2) {
							GUIUtility.ExitGUI();
						}

						reparentChildren = option == 0;
					}

					StringBuilder replaceReport = new StringBuilder(300);

					if (data.ReplacePrefab != null) {
						replaceReport.AppendLine($"Search For: \"{data.Root.Name}\"; Replace With: \"{data.ReplacePrefab.name}\"; Reparent: {reparentChildren}");
					} else {
						replaceReport.AppendLine($"Search and delete: \"{data.Root.Name}\"");
					}

					ReplaceSinglePrefabResult(data, reparentChildren, replaceReport);

					Debug.Log($"Replace report:\n" + replaceReport, data.Root.ToUnityObject());
				}

				EditorGUILayout.EndHorizontal();
			}
		}

		private void DrawMoreResultsOperation()
		{
			EditorGUILayout.BeginHorizontal();

			EditorGUI.BeginDisabledGroup(m_ResultsHistoryIndex < 1);

			if (GUILayout.Button(CorelateButton, EditorStyles.miniButton)) {
				SearchResult startResults = m_CurrentResults;
				SearchResult endResults = m_ResultsHistory[m_ResultsHistoryIndex - 1];

				AddNewResultsEntry(new SearchResult());

				foreach (var target in endResults.SearchResults) {
					m_CurrentResults.Add(target.Root, new SearchResultData() { Root = target.Root });
				}

				foreach (AssetHandle startFoundAsset in startResults.SearchResults.SelectMany(sr => sr.Found)) {
					string[] dependencies = AssetDatabase.GetDependencies(startFoundAsset.AssetPath);

					foreach (string dependencyPath in dependencies) {
						var searchResult = m_CurrentResults.SearchResults.FirstOrDefault(sr => sr.Root.AssetPath == dependencyPath);
						if (searchResult != null && !searchResult.Found.Contains(startFoundAsset)) {
							searchResult.Found.Add(startFoundAsset);
						}
					}
				}

				foreach(var result in m_CurrentResults.SearchResults) {
					foreach(var found in result.Found) {
						var obj = found.ToUnityObject();
						if (obj) {
							m_CurrentResults.TryAddType(obj.GetType());
						}

						m_CurrentResults.AddToCombinedList(found, result.Root);
					}
				}
			}

			EditorGUI.EndDisabledGroup();

			if (m_ResultsViewMode == ResultsViewMode.SearchResults) {
				DrawReplaceAllPrefabs();
			}

			GUILayout.FlexibleSpace();

			if (ResultProcessors.Count > 0) {
				string[] processorNames = ResultProcessors.Select(rp => rp.Name).ToArray();

				m_SelectedResultProcessor = EditorGUILayout.Popup(m_SelectedResultProcessor, processorNames, GUILayout.Width(150));

				if (GUILayout.Button(EditorGUIUtility.IconContent("PlayButton"), GUILayout.ExpandWidth(false)) && m_CurrentResults != null) {
					var results = m_CurrentResults.SearchResults
						.Where(
							rd => rd.Root.Exists() &&
								  (string.IsNullOrEmpty(m_ResultsSearchEntryFilter) ||
								   rd.Root.Name.IndexOf(m_ResultsSearchEntryFilter, StringComparison.OrdinalIgnoreCase) !=
								   -1))
						.SelectMany(rd => rd.Found.Select(f => f.ToUnityObject()))
						.Where(
							obj => obj != null &&
								  (string.IsNullOrEmpty(m_ResultsFoundEntryFilter) ||
								   obj.name.IndexOf(m_ResultsFoundEntryFilter, StringComparison.OrdinalIgnoreCase) != -1));

					ResultProcessors[m_SelectedResultProcessor].ProcessResults(results);
				}
			}

			EditorGUILayout.EndHorizontal();
		}

		private void DrawReplaceAllPrefabs()
		{
			bool enableReplaceButton = m_CurrentResults != null && m_CurrentResults.SearchResults.Any(pair => pair.Root.IsPrefab && pair.Found.Any(ah => ah.IsScene));
			EditorGUI.BeginDisabledGroup(!enableReplaceButton);
			if (GUILayout.Button(ReplacePrefabsAllButton, EditorStyles.miniButton, GUILayout.ExpandWidth(false))) {

				var option = EditorUtility.DisplayDialogComplex("Reparent objects?",
					"If prefab has other game objects attached to its children, what should I do with them?",
					"Re-parent",
					"Destroy",
					"Cancel");

				if (option == 2) {
					GUIUtility.ExitGUI();
				}

				if (!EditorUtility.DisplayDialog("Are you sure?",
						"This will replace all searched prefabs with the ones specified for replacing, in whichever scenes they were found. If nothing is specified, no replacing will occur.\n\nAre you sure?",
						"Do it!",
						"Cancel")) {
					GUIUtility.ExitGUI();
				}


				StringBuilder replaceReport = new StringBuilder(300);
				replaceReport.AppendLine($"Mass replace started! Reparent: {option == 0}");
				ReplaceAllPrefabResults(m_CurrentResults, option == 0, replaceReport);

				Debug.Log($"Replace report:\n" + replaceReport);
			}
			EditorGUI.EndDisabledGroup();
		}


		private string GetSaveSlothPath(int index)
		{
			return Application.temporaryCachePath + "/" + $"SearchReferencesFast_Slot_{index}";
		}

		private void DrawSaveResultsSlots()
		{
			const int SLOTS_COUNT = 5;
			for (int i = 0; i < SLOTS_COUNT; ++i) {
				if (GUILayout.Button(i.ToString(), GUILayout.ExpandWidth(false))) {



					var option = EditorUtility.DisplayDialogComplex("Save/Load?",
						$"You have selected save slot {i}.\nYou can save to or load from it results.",
						"Save",
						"Load",
						"Cancel");

					switch (option) {
						case 0:
							var serializer = new BinaryFormatter();

							using (FileStream fileStream = File.Open(GetSaveSlothPath(i), FileMode.OpenOrCreate)) {
								serializer.Serialize(fileStream, m_CurrentResults);
							}

							break;

						case 1:
							if (!File.Exists(GetSaveSlothPath(i))) {
								EditorUtility.DisplayDialog("Load failed", $"No saved results were found at slot {i}.", "Ok");
								break;
							}

							serializer = new BinaryFormatter();

							using (FileStream fileStream = File.Open(GetSaveSlothPath(i), FileMode.Open)) {

								try {
									AddNewResultsEntry((SearchResult)serializer.Deserialize(fileStream));
								}
								catch (Exception ex) {
									Debug.LogException(ex);
									EditorUtility.DisplayDialog("Error", $"Could not load saved results from slot {i}.\nProbably the data format changed.\nFor details check the logs.", "Ok");
								}
							}
							break;
					}

				}
			}
		}

		private static void ReplaceSinglePrefabResult(SearchResultData searchResultData, bool reparentChildren, StringBuilder replaceReport)
		{
			Debug.Assert(searchResultData.Root.Exists());

			var scenes = searchResultData
					.Found
					.Select(ah => ah.ToUnityObject())
					.OfType<SceneAsset>()
					.ToList()
				;

			ReplacePrefabResultsInScenes(scenes, new List<SearchResultData>() { searchResultData }, reparentChildren, replaceReport);
		}

		private static void ReplaceAllPrefabResults(SearchResult searchResult, bool reparentChildren, StringBuilder replaceReport)
		{
			var resultDataToReplace = searchResult
				.SearchResults
				.Where(data => data.Root.Exists())
				.Where(data => data.ReplacePrefab != null)
				.ToList()
				;

			var scenes = resultDataToReplace
				.SelectMany(data => data.Found.Select(ah => ah.ToUnityObject()).OfType<SceneAsset>())
				.Distinct()
				.ToList()
				;

			ReplacePrefabResultsInScenes(scenes, resultDataToReplace, reparentChildren, replaceReport);
		}


		// Replace one prefab in many scenes or many prefabs in many scenes.
		private static void ReplacePrefabResultsInScenes(List<SceneAsset> scenes, List<SearchResultData> resultDataToReplace, bool reparentChildren, StringBuilder replaceReport)
		{
			for (int i = 0; i < scenes.Count; ++i) {
				var sceneAsset = scenes[i];

				bool cancel = EditorUtility.DisplayCancelableProgressBar("Replacing...", $"{sceneAsset.name}", (float)i / scenes.Count);
				if (cancel) {
					EditorUtility.ClearProgressBar();
					break;
				}

				EditorSceneManager.OpenScene(AssetDatabase.GetAssetPath(sceneAsset));

				var scene = EditorSceneManager.GetActiveScene();
				foreach (var sceneRoot in scene.GetRootGameObjects()) {

					foreach (var transform in sceneRoot.GetComponentsInChildren<Transform>(true)) {

						// When replacing, we destroy the old object, so this transform could become null while iterating.
						if (transform == null)
							continue;

						var go = transform.gameObject;

#if UNITY_2018_2_OR_NEWER
						var foundPrefab = PrefabUtility.GetCorrespondingObjectFromSource(go);
#else
						var foundPrefab = PrefabUtility.GetPrefabParent(go);
#endif
						// If the found prefab matches any of the requested (only prefab roots).
						var data = resultDataToReplace.FirstOrDefault(d => d.Root.ToUnityObject() == foundPrefab);
						if (data != null) {

							// Store sibling index before reparenting children.
							int nextSiblingIndex = transform.GetSiblingIndex() + 1;

							if (reparentChildren) {
								var reparented = new List<GameObject>();
								ReparentForeignObjects(go, transform.parent, transform, reparented);

								if (reparented.Count > 0) {
									replaceReport.AppendLine($"> Re-parented: {string.Join(",", reparented.Select(g => g.name))}");
								}
							}

							if (data.ReplacePrefab != null) {
								replaceReport.AppendLine($"Scene: {sceneAsset.name}; Replaced: {GetGameObjectPath(go)};");

								var replaceInstance = (GameObject)PrefabUtility.InstantiatePrefab(data.ReplacePrefab);
								replaceInstance.transform.SetParent(transform.parent);
								replaceInstance.transform.localPosition = transform.localPosition;
								replaceInstance.transform.localRotation = transform.localRotation;
								replaceInstance.transform.localScale = transform.localScale;
								replaceInstance.SetActive(go.activeSelf);
								replaceInstance.transform.SetSiblingIndex(nextSiblingIndex);
							} else {
								replaceReport.AppendLine($"Scene: {sceneAsset.name}; Deleted: {GetGameObjectPath(go)};");
							}

							DestroyImmediate(go);
						}
					}
				}

				EditorSceneManager.SaveScene(scene);
			}


			foreach (var data in resultDataToReplace) {
				data.Found.RemoveAll(obj => obj.ToUnityObject() is SceneAsset && scenes.Contains(obj.ToUnityObject()));
			}

			EditorUtility.ClearProgressBar();

			EditorUtility.DisplayDialog("Complete", "Prefabs were replaced. Please check the replace report in the logs.", "I will!");
		}

		private void AddNewResultsEntry(SearchResult results)
		{
			if (results.SearchTargetEntries.Length > 0) {
				m_ResultsHistory.RemoveAll(sr => sr.EqualSearchTargets(results));
			}

			m_ResultsHistory.Add(results);
			if (m_ResultsHistory.Count > 30) {
				m_ResultsHistory.RemoveAt(0);
			}
			m_ResultsHistoryIndex = m_ResultsHistory.Count - 1;
		}

		private void SelectPreviousResults()
		{
			if (m_ResultsHistoryIndex >= 0) {
				m_ResultsHistoryIndex--;
			}
		}

		private void SelectNextResults()
		{
			if (m_ResultsHistoryIndex < m_ResultsHistory.Count - 1) {
				m_ResultsHistoryIndex++;
			}
		}

		private static string GetGameObjectPath(GameObject go)
		{
			var transform = go.transform;

			var path = new List<string>();

			while (transform) {
				path.Add(transform.name);
				transform = transform.parent;
			}

			path.Reverse();

			return string.Join("/", path);
		}



		private static bool ReparentForeignObjects(GameObject root, Transform targetParent, Transform transform, List<GameObject> reparented)
		{
			if (PrefabUtility.GetOutermostPrefabInstanceRoot(transform.gameObject) != root) {
				transform.parent = targetParent;
				reparented.Add(transform.gameObject);
				return true;
			}

			for (int i = 0; i < transform.childCount; ++i) {
				if (ReparentForeignObjects(root, targetParent, transform.GetChild(i), reparented)) {
					--i;
				}
			}

			return false;
		}

		private Vector2 m_PreferencesScroll;
		private void DrawPreferences()
		{
			EditorGUILayout.LabelField("Preferences:", EditorStyles.boldLabel);

			m_PreferencesScroll = EditorGUILayout.BeginScrollView(m_PreferencesScroll, GUILayout.ExpandHeight(false));

			var sp = m_SerializedObject.FindProperty("_searchFilter").FindPropertyRelative("ExcludePreferences");

			EditorGUILayout.PropertyField(sp, new GUIContent("Exclude paths or file names for this project:"), true);

			EditorGUILayout.EndScrollView();

			if (GUILayout.Button("Done", GUILayout.ExpandWidth(false))) {
				m_SearchFilter.ExcludePreferences.RemoveAll(string.IsNullOrWhiteSpace);

				File.WriteAllLines(PROJECT_EXCLUDES_PATH, m_SearchFilter.ExcludePreferences);
				GUI.FocusControl("");
				m_ShowPreferences = false;
			}
		}

		[Serializable]
		private struct AssetHandle
		{
			public string Guid;
			public string LocalId;
			public bool IsSubAsset;
			public string AssetPath;
			public string Name;

			public bool IsPrefab => AssetPath.EndsWith(".prefab");
			public bool IsScene => AssetPath.EndsWith(".unity");

			public AssetHandle(string guid, string localId)
			{
				Guid = guid;
				LocalId = localId;
				IsSubAsset = !string.IsNullOrEmpty(localId);
				AssetPath = AssetDatabase.GUIDToAssetPath(guid);
				Name = Path.GetFileName(AssetPath);
			}

			public AssetHandle(Object obj)
			{
				IsSubAsset = AssetDatabase.IsSubAsset(obj);

				AssetDatabase.TryGetGUIDAndLocalFileIdentifier(obj, out string guid, out long localId);
				Guid = guid;
				//LocalId = IsSubAsset ? localId.ToString() : "";	// Even main assets have localId that is useable.
				LocalId = localId.ToString();

				AssetPath = AssetDatabase.GetAssetPath(obj);
				Name = IsSubAsset ? obj.name : Path.GetFileName(AssetPath);

			}

			public bool Equals(AssetHandle other)
			{
				if (ReferenceEquals(this, other))
					return true;

				if (ReferenceEquals(other, null))
					return false;

				return Guid == other.Guid
					&& LocalId == other.LocalId
					&& IsSubAsset == other.IsSubAsset
					&& AssetPath == other.AssetPath
					&& Name == other.Name
					;
			}

			public bool Exists() => ToUnityObject() != null;

			public Object ToUnityObject()
			{
				if (string.IsNullOrEmpty(Guid))
					return null;

				if (IsSubAsset) {
					long localId = long.Parse(LocalId);

					var subAssets = AssetDatabase.LoadAllAssetRepresentationsAtPath(AssetDatabase.GUIDToAssetPath(Guid));
					foreach (Object subAsset in subAssets) {
						AssetDatabase.TryGetGUIDAndLocalFileIdentifier(subAsset, out string _, out long subAssetId);

						if (localId == subAssetId) {
							return subAsset;
						}
					}

					return null;

				} else {
					return AssetDatabase.LoadMainAssetAtPath(AssetDatabase.GUIDToAssetPath(Guid));
				}
			}
		}

		[Serializable]
		private class SearchEntryData
		{
			[field: SerializeField] public string TargetText = "";  // For searching by text.
			[field: SerializeField] public AssetHandle Target;

			public SearchEntryData() { }

			public SearchEntryData(Object target)
			{
				Target = new AssetHandle(target);

				// LocalId is 102900000 for folders, scenes and unknown asset types. Probably marks this as invalid id.
				// For unknown asset types, which actually have some localIds inside them (custom made), this will be invalid when linked in places.
				// Example: Custom generated mesh files that start with "--- !u!43 &4300000" at the top, will actually use 4300000 in the reference when linked somewhere.
				if (Target.LocalId == "102900000") {
					Target.LocalId = "";
				}

				// Prefabs don't have localId (or rather it is not used in the scenes at least).
				// We don't support searching for nested game objects of a prefab.
				if (target is GameObject) {
					Target.LocalId = "";
				}
			}

			public SearchEntryData(string targetText)
			{
				TargetText = targetText;
				Target = new AssetHandle(AssetDatabase.LoadMainAssetAtPath("Assets"));	// We still need some Target to show (so the rest of the code can work).
			}

			public bool Equals(SearchEntryData other)
			{
				if (ReferenceEquals(this, other))
					return true;

				if (ReferenceEquals(other, null))
					return false;

				return TargetText.Equals(other.TargetText)
					&& Target.Equals(other.Target)
					;
			}
		}

		[Serializable]
		private class SearchResult
		{
			public List<SearchResultData> SearchResults = new List<SearchResultData>();	// Results per search object
			public List<string> ResultTypesNames = new List<string>();
			public List<SearchResultData> CombinedFoundList = new List<SearchResultData>(); // Searches per result object

			public SearchMetas SearchMetas = SearchMetas.SearchWithMetas;
			public bool SearchMainAssetOnly = false;
			public SearchEntryData[] SearchTargetEntries = new SearchEntryData[0];
			public SearchAssetsFilter SearchFilter = new SearchAssetsFilter();

			public void Reset()
			{
				SearchResults.Clear();
				ResultTypesNames.Clear();
				CombinedFoundList.Clear();
			}

			public bool EqualSearchTargets(SearchResult other)
			{
				if (ReferenceEquals(this, other))
					return true;

				if (ReferenceEquals(other, null))
					return false;

				if (SearchMetas != other.SearchMetas)
					return false;

				if (SearchMainAssetOnly != other.SearchMainAssetOnly)
					return false;

				if (SearchTargetEntries.Length != other.SearchTargetEntries.Length)
					return false;

				foreach(SearchEntryData entry in SearchTargetEntries) {
					if (!other.SearchTargetEntries.Any(oe => oe.Equals(entry)))
						return false;
				}

				return SearchFilter.Equals(other.SearchFilter);
			}

			public bool TryGetValue(AssetHandle key, out SearchResultData data)
			{
				var index = SearchResults.FindIndex(p => p.Root.Equals(key));
				if (index == -1) {
					data = null;
					return false;
				} else {
					data = SearchResults[index];
					return true;
				}

			}

			public SearchResultData this[AssetHandle searchObjectKey] {
				get {
					SearchResultData data;
					if (!TryGetValue(searchObjectKey, out data)) {
						throw new InvalidDataException("Key is missing!");
					}
					return data;
				}
			}

			public void Add(AssetHandle key, SearchResultData data)
			{
				if (string.IsNullOrEmpty(key.Guid)) {
					throw new ArgumentNullException("Invalid key not allowed!");
				}

				if (SearchResults.Any(p => p.Root.Equals(key))) {
					throw new ArgumentException($"Key {key.AssetPath} already exists!");
				}

				SearchResults.Add(data);
			}

			public void TryAddType(Type type)
			{
				if (!ResultTypesNames.Contains(type.Name)) {
					ResultTypesNames.Add(type.Name);
				}
			}

			public void AddToCombinedList(AssetHandle foundLocation, AssetHandle searchSource)
			{
				foreach(var combinedData in CombinedFoundList) {
					if (foundLocation.Equals(combinedData.Root)) {
						combinedData.Found.Add(searchSource);
						return;
					}
				}

				CombinedFoundList.Add(new SearchResultData() { Root = foundLocation });
				CombinedFoundList.Last().Found.Add(searchSource);
			}
		}

		[Serializable]
		private class SearchResultData : ISerializable
		{
			// NOTE: This class is re-used for storing and rendering combined results - searches per result object.
			public AssetHandle Root;
			public List<AssetHandle> Found = new List<AssetHandle>(10);

			// GUI
			public bool ShowDetails = true;
			public GameObject ReplacePrefab;


			#region Serialization

			public SearchResultData()
			{ }

			private SearchResultData(SerializationInfo info, StreamingContext context)
			{
				Root = (AssetHandle) info.GetValue(nameof(Root), typeof(AssetHandle));
				Found = ((AssetHandle[])info.GetValue(nameof(Found), typeof(AssetHandle[]))).ToList();

				ShowDetails = info.GetBoolean(nameof(ShowDetails));
				ReplacePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(info.GetString(nameof(ReplacePrefab))));
			}


			public void GetObjectData(SerializationInfo info, StreamingContext context)
			{
				info.AddValue(nameof(Root), Root);
				info.AddValue(nameof(Found), Found.ToArray());

				info.AddValue(nameof(ShowDetails), ShowDetails);
				info.AddValue(nameof(ReplacePrefab), AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(ReplacePrefab)));
			}

			#endregion
		}

		private class ProgressHandle
		{
			public int ItemsDone = 0;
			public readonly int ItemsTotal = 0;
			public string LastProcessedPath;

			public bool CancelRequested = false;

			public bool Finished => ItemsDone == ItemsTotal;

			public ProgressHandle(int length)
			{
				ItemsTotal = length;
			}

			public float Progress01 {
				get {
					if (ItemsTotal == 0)
						return 1f;

					return (float)ItemsDone / ItemsTotal;
				}
			}

			public int ProgressPercentage {
				get {
					if (ItemsTotal == 0)
						return 100;

					return Mathf.RoundToInt(((float)ItemsDone / ItemsTotal) * 100f);
				}
			}
		}

		private class FileBuffers
		{
			public readonly StringBuilder StringBuilder = new StringBuilder(4 * 1024);
			public readonly char[] Buffer = new char[4 * 1024];

			public void Clear()
			{
				StringBuilder.Clear();
			}

			public void AppendFile(string path)
			{
				using (StreamReader streamReader = new StreamReader(path, Encoding.UTF8, true, Buffer.Length)) {

					while (true) {
						int charsRead = streamReader.ReadBlock(Buffer, 0, Buffer.Length);

						if (charsRead == 0) {
							break;
						}

						StringBuilder.Append(Buffer, 0, charsRead);
					}
				}
			}
		}
	}
}
