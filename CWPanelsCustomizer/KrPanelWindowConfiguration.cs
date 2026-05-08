using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CWPanelsCustomizer.Helpers;

namespace CWPanelsCustomizer
{
    [Transaction(TransactionMode.Manual)]
    public partial class CurtainPanelWindowConfiguration : IExternalCommand
    {
        public static string IS_TAB_NAME => "КР";
        public static string IS_NAME => "Заменить АР кассеты на КР";
        public static string IS_DESCRIPTION => "*Что делает плагин?";
        public static string IS_IMAGE => "CWPanelsCustomizer.Images.a1.png";

        private UIDocument _uidoc;
        private Document _doc;
        private RevitLogger _logger;
        private UIApplication _uiapp;

        // --- Режим работы по выделению ---
        private enum PluginSelectionMode { All, ByWalls, ByPanels }
        private PluginSelectionMode _selMode = PluginSelectionMode.All;
        private HashSet<ElementId> _selectedWallIds;   // ByWalls
        private HashSet<ElementId> _selectedPanelIds;  // ByPanels

        // --- Кеши (заполняются в RunPlugin, до старта транзакций) ---
        private Dictionary<string, FamilySymbol> _symbolCache;  // "FamilyName/TypeName" → FamilySymbol
        private List<Wall> _curtainWalls;                        // все витражные стены проекта

        // --- Счётчики текущего запуска ReplaceArCurtainPanelsWithKrPanels (сбрасываются в начале) ---
        private int _arReplaced, _arSkippedKrType, _arSkippedInvalid, _arFailed;
        private int _arOffsetsOk, _arOffsetsFail, _arMatsOk, _arMatsFail, _arMat2Ok, _arMat2Fail;

        private const double EPS = 1e-9;
        private const double FEET_TO_MM = 304.8;

        private static double MmToFt(double mm) => mm / FEET_TO_MM;

        // Коррекция смещения: неточность семейства КР-панели — панель утоплена на 13 мм
        private const double KR_FAMILY_OFFSET_CORRECTION_MM = 13.0;

        private const double WINDOW_CUTOUT_SCALE = 0.0;

        private const string REGULAR_PANEL_FAMILY_NAME = "КРСТ_НВФ_Уголвая_В2.1";
        private const string REGULAR_PANEL_FAMILY_NAME_TYPE = "RAL 5005";

        private const string G_PANEL_FAMILY_NAME = "КРСТ_НВФ_С Г-образным вырезом_В2";
        private const string G_PANEL_FAMILY_NAME_TYPE = "RAL 5005";

        private const string L_PANEL_FAMILY_NAME = "КРСТ_НВФ_С L-образным вырезом";
        private const string L_PANEL_FAMILY_NAME_TYPE = "RAL 5005";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            return RunPluginCore(commandData.Application);
        }

        private Result RunPluginCore(UIApplication uiapp)
        {
            _uiapp = uiapp;
            _uidoc = uiapp.ActiveUIDocument;
            _doc = _uidoc.Document;
            _logger = RevitLogger.GetLogger(_doc);
            _logger.BeginSession(IS_NAME, _doc.Title);

            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                RunPlugin();
                TaskDialog.Show(IS_NAME, $"Замена АР кассет на КР завершена.\nВремя выполнения: {sw.Elapsed.TotalSeconds:F1} сек.");
                _logger.Info("Execution time: " + sw.ElapsedMilliseconds + "ms");
                _logger.EndSession("Succeeded");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                _logger.Error("FAILED", ex);
                _logger.Info("Execution time: " + sw.ElapsedMilliseconds + "ms");
                _logger.EndSession("Failed");
                throw;
            }
        }

        private void RunPlugin()
        {
            DetectSelectionMode();

            // Кеш FamilySymbol (OST_CurtainWallPanels) — один обход базы для всех поисков
            _symbolCache = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Where(fs => fs.Category?.Id?.IntegerValue == (int)BuiltInCategory.OST_CurtainWallPanels)
                .GroupBy(fs => $"{fs.Family?.Name ?? string.Empty}/{fs.Name ?? string.Empty}")
                .ToDictionary(g => g.Key, g => g.First());
            _logger.Info($"[RunPlugin] _symbolCache built: {_symbolCache.Count} symbols");

            // Кеш витражных стен — один обход базы
            _curtainWalls = new FilteredElementCollector(_doc)
                .OfClass(typeof(Wall)).Cast<Wall>()
                .Where(w => w.CurtainGrid != null).ToList();
            _logger.Info($"[RunPlugin] _curtainWalls built: {_curtainWalls.Count} walls");

            using (TransactionGroup tg = new TransactionGroup(_doc, IS_NAME))
            {
                tg.Start();

                // 1) Замена АР кассет на КР
                ReplaceArCurtainPanelsWithKrPanels(_doc);

                // 2) Сбор данных
                List<CurtainWallDataDto> data = GetElements(_doc);

                // 2.5) Конвертация панелей внутри проёмов в пустые
                ConvertPanelsInsideOpeningsToEmpty(data);

                // 3) Сброс подрезок рядовых панелей по пересечению с проёмами
                ResetRegularPanelsCutsForIntersectingOpenings(data);

                // 4) Замена рядовых панелей на угловые (где нужно)
                ReplaceRegularPanelsWithCutoutPanels(data);

                // 5) Отзеркаливание панелей справа от окна, пересекающихся с BB окна
                MirrorPanelsRightOfOpenings(data);

                // 6) Подрезки рядовых панелей
                CalculateAndSetRegularPanelsCuts(data);

                // 7) Настройка угловых панелей по значениям рядовых
                CalculateAndSetCutoutPanelsCuts(data);

                int totalOpenings = GetTotalOpeningsCount(_doc);
                int totalCurtainWalls = GetTotalCurtainWallsCount(_doc);
                int wallsInWork = data.Count;
                int totalAssignedOpenings = data.Sum(x => x.IntersectingOpenings.Count);

                _logger.LogSummary("Result",
                    ("TotalOpenings", totalOpenings),
                    ("TotalCurtainWalls", totalCurtainWalls),
                    ("WallsInWork", wallsInWork),
                    ("AssignedOpenings", totalAssignedOpenings));

                tg.Assimilate();
            }
        }

        private void DetectSelectionMode()
        {
            _selMode = PluginSelectionMode.All;
            _selectedWallIds = null;
            _selectedPanelIds = null;

            var selectedIds = _uidoc.Selection.GetElementIds().ToList();
            if (selectedIds.Count == 0)
            {
                _logger.Info("[Selection] Mode=All (nothing selected)");
                return;
            }

            // Витражи?
            var selectedWalls = selectedIds
                .Select(id => _doc.GetElement(id) as Wall)
                .Where(w => w != null && w.CurtainGrid != null)
                .ToList();

            if (selectedWalls.Count > 0)
            {
                _selMode = PluginSelectionMode.ByWalls;
                _selectedWallIds = new HashSet<ElementId>(selectedWalls.Select(w => w.Id));
                _logger.Info($"[Selection] Mode=ByWalls, walls={_selectedWallIds.Count}");
                return;
            }

            // Панели витража?
            var panelCategoryId = new ElementId((int)BuiltInCategory.OST_CurtainWallPanels);
            var selectedPanels = selectedIds
                .Where(id =>
                {
                    Element e = _doc.GetElement(id);
                    return e != null && e.Category != null && e.Category.Id == panelCategoryId;
                })
                .ToList();

            if (selectedPanels.Count > 0)
            {
                _selMode = PluginSelectionMode.ByPanels;
                _selectedPanelIds = new HashSet<ElementId>(selectedPanels);
                _logger.Info($"[Selection] Mode=ByPanels, panels={_selectedPanelIds.Count}");
                return;
            }

            // Выделено что-то другое → по всему проекту
            _logger.Info($"[Selection] Mode=All (selected {selectedIds.Count} non-CW elements)");
        }

        private void ReplaceArCurtainPanelsWithKrPanels(Document doc)
        {
            const string TAG = "[ReplaceArCurtainPanelsWithKrPanels]";

            _arReplaced = _arSkippedKrType = _arSkippedInvalid = _arFailed = 0;
            _arOffsetsOk = _arOffsetsFail = _arMatsOk = _arMatsFail = _arMat2Ok = _arMat2Fail = 0;

            // 1) Сбор АР-панелей
            var (arPanelIds, panelToCwInfo) = CollectArPanelData(doc, TAG);
            if (arPanelIds.Count == 0) return;

            // 2) Поиск и активация целевого КР-символа
            FamilySymbol targetSymbol = FindAndActivateTargetSymbol(doc, TAG);

            // 3) TX1: замена типов + перенос параметров FI-панелей
            var wallPending = ExecuteTx1_ReplaceTypes(doc, TAG, arPanelIds, targetSymbol, panelToCwInfo);

            // 4) TX2: перенос параметров Wall→FI панелей (матчинг по BB)
            if (wallPending.Count > 0)
                ExecuteTx2_TransferOffsets(doc, TAG, wallPending);

            // Итог
            _logger.Info($"{TAG} SUMMARY:");
            _logger.Info($"{TAG}  AR panels processed: {arPanelIds.Count}");
            _logger.Info($"{TAG}  Replaced (AR->KR): {_arReplaced}");
            _logger.Info($"{TAG}  Offsets transferred: {_arOffsetsOk}, failed: {_arOffsetsFail}");
            _logger.Info($"{TAG}  Materials transferred: {_arMatsOk}, failed: {_arMatsFail}");
            _logger.Info($"{TAG}  MaterialParam2 (Кассета_Материал отделки): {_arMat2Ok}, failed: {_arMat2Fail}");
            _logger.Info($"{TAG}  Skipped (already KR type): {_arSkippedKrType}");
            _logger.Info($"{TAG}  Skipped (invalid): {_arSkippedInvalid}");
            _logger.Info($"{TAG}  Failed: {_arFailed}");
        }

        private (List<ElementId> arPanelIds, Dictionary<ElementId, (XYZ normal, int cwIdInt)> panelToCwInfo)
            CollectArPanelData(Document doc, string tag)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));

            const string AR_TYPE_PREFIX_1 = "AP_";
            const string AR_TYPE_PREFIX_2 = "АР";
            const string AR_TYPE_PREFIX_3 = "Кассета";
            const string AR_FI_FAMILY     = "Системная панель";

            // 1) Собираем все Wall и FI панели витражей
            var allWallPanels = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_CurtainWallPanels)
                .WhereElementIsNotElementType().OfType<Wall>().ToList();

            var allFiPanels = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_CurtainWallPanels)
                .WhereElementIsNotElementType().OfType<FamilyInstance>().ToList();

            // 2) Фильтр по режиму выделения
            List<ElementId> wallPanelIds, fiPanelIds;
            if (_selMode == PluginSelectionMode.ByWalls && _selectedWallIds != null && _selectedWallIds.Count > 0)
            {
                fiPanelIds = allFiPanels
                    .Where(fi => fi.Host != null && _selectedWallIds.Contains(fi.Host.Id))
                    .Select(fi => fi.Id).ToList();
                var wallPanelSet = new HashSet<ElementId>();
                foreach (ElementId wallId in _selectedWallIds)
                {
                    Wall cw = doc.GetElement(wallId) as Wall;
                    if (cw == null) continue;
                    foreach (ElementId depId in cw.GetDependentElements(new ElementClassFilter(typeof(Wall))))
                        wallPanelSet.Add(depId);
                }
                wallPanelIds = allWallPanels.Where(w => wallPanelSet.Contains(w.Id)).Select(w => w.Id).ToList();
            }
            else
            {
                wallPanelIds = allWallPanels.Select(w => w.Id).ToList();
                fiPanelIds   = allFiPanels.Select(fi => fi.Id).ToList();
            }
            _logger.Info($"{tag} Found Wall panels: {wallPanelIds.Count}, FamilyInstance panels: {fiPanelIds.Count}");

            // 3) Отбор АР-панелей
            var arPanelIds = new List<ElementId>();
            int skippedNotAr = 0, skippedInvalidAtScan = 0;

            foreach (ElementId id in wallPanelIds)
            {
                Element e = doc.GetElement(id);
                if (e == null || !e.IsValidObject) { skippedInvalidAtScan++; continue; }
                Wall w = e as Wall;
                if (w == null) { skippedInvalidAtScan++; continue; }
                ElementType t = doc.GetElement(w.GetTypeId()) as ElementType;
                if (t == null) { skippedInvalidAtScan++; continue; }
                string typeName = t.Name ?? string.Empty;
                bool isAr = typeName.StartsWith(AR_TYPE_PREFIX_1, StringComparison.OrdinalIgnoreCase)
                         || typeName.StartsWith(AR_TYPE_PREFIX_2, StringComparison.OrdinalIgnoreCase)
                         || typeName.StartsWith(AR_TYPE_PREFIX_3, StringComparison.OrdinalIgnoreCase);
                if (isAr) arPanelIds.Add(id); else skippedNotAr++;
            }

            foreach (ElementId id in fiPanelIds)
            {
                Element e = doc.GetElement(id);
                if (e == null || !e.IsValidObject) { skippedInvalidAtScan++; continue; }
                FamilyInstance fi = e as FamilyInstance;
                if (fi == null) { skippedInvalidAtScan++; continue; }
                string famName  = fi.Symbol?.Family?.Name ?? string.Empty;
                string typeName = fi.Symbol?.Name ?? string.Empty;
                bool isAr = string.Equals(famName, AR_FI_FAMILY, StringComparison.OrdinalIgnoreCase)
                         && typeName.StartsWith(AR_TYPE_PREFIX_3, StringComparison.OrdinalIgnoreCase);
                if (isAr) arPanelIds.Add(id); else skippedNotAr++;
            }

            _logger.Info($"{tag} AR panels total: {arPanelIds.Count} (skippedNotAr={skippedNotAr}, skippedInvalid={skippedInvalidAtScan})");

            // Фильтр ByPanels
            if (_selMode == PluginSelectionMode.ByPanels && _selectedPanelIds != null)
            {
                int before = arPanelIds.Count;
                arPanelIds = arPanelIds.Where(id => _selectedPanelIds.Contains(id)).ToList();
                _logger.Info($"{tag} ByPanels filter: {before} → {arPanelIds.Count} AR panels");
            }

            // Диагностика если ничего нет
            if (arPanelIds.Count == 0)
            {
                var sampleFamilies = allFiPanels
                    .Where(fi => _selMode != PluginSelectionMode.ByWalls ||
                                 (_selectedWallIds != null && fi.Host != null && _selectedWallIds.Contains(fi.Host.Id)))
                    .GroupBy(fi => $"{fi.Symbol?.FamilyName}/{fi.Symbol?.Name}")
                    .OrderByDescending(g => g.Count()).Take(5)
                    .Select(g => $"{g.Key} ×{g.Count()}");
                _logger.Info($"{tag} No AR panels. Top families in scope: {string.Join("; ", sampleFamilies)}");
            }

            // 4) Строим panelToCwInfo: Wall-панель → (нормаль витража, Id витража)
            var panelToCwInfo = new Dictionary<ElementId, (XYZ normal, int cwIdInt)>();
            var allWallPanelIds = new HashSet<ElementId>(allWallPanels.Select(w => w.Id));
            foreach (var cw in _curtainWalls)
            {
                XYZ n = (cw.Orientation ?? XYZ.BasisY).Normalize();
                int cwId = cw.Id.IntegerValue;
                foreach (var depId in cw.GetDependentElements(new ElementClassFilter(typeof(Wall))))
                    if (allWallPanelIds.Contains(depId))
                        panelToCwInfo[depId] = (n, cwId);
            }
            _logger.Info($"{tag} panelToCwInfo built: {panelToCwInfo.Count} Wall panels mapped");

            return (arPanelIds, panelToCwInfo);
        }

        private FamilySymbol FindAndActivateTargetSymbol(Document doc, string tag)
        {
            const string TARGET_FAMILY = REGULAR_PANEL_FAMILY_NAME;
            const string TARGET_TYPE   = REGULAR_PANEL_FAMILY_NAME_TYPE;

            string targetKey = $"{TARGET_FAMILY}/{TARGET_TYPE}";
            _symbolCache.TryGetValue(targetKey, out FamilySymbol targetSymbol);
            if (targetSymbol == null)
                targetSymbol = _symbolCache.Values.FirstOrDefault(fs =>
                    string.Equals(fs.Family?.Name, TARGET_FAMILY, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(fs.Name, TARGET_TYPE, StringComparison.OrdinalIgnoreCase));

            if (targetSymbol == null)
                throw new InvalidOperationException(
                    $"{tag} Target KR panel type not found. " +
                    $"Expected Family='{TARGET_FAMILY}', Type='{TARGET_TYPE}' in OST_CurtainWallPanels.");

            _logger.Info($"{tag} Target symbol: Family='{targetSymbol.Family.Name}', Type='{targetSymbol.Name}', Id={targetSymbol.Id.IntegerValue}");

            if (!targetSymbol.IsActive)
            {
                using (Transaction tx = new Transaction(doc, "Activate target KR panel type"))
                {
                    tx.Start();
                    targetSymbol.Activate();
                    tx.Commit();
                }
            }

            return targetSymbol;
        }

        private List<(XYZ bbMin, XYZ bbMax, double offsetFt, XYZ wallNormal, int cwIdInt, int materialIdInt)>
            ExecuteTx1_ReplaceTypes(Document doc, string tag, List<ElementId> arPanelIds, FamilySymbol targetSymbol,
                Dictionary<ElementId, (XYZ normal, int cwIdInt)> panelToCwInfo)
        {
            ElementId targetTypeId = targetSymbol.Id;
            var wallPendingOffsets = new List<(XYZ, XYZ, double, XYZ, int, int)>();

            using (Transaction tx = new Transaction(doc, "Replace ONLY AR curtain panels with KR panels (Family+Type)"))
            {
                tx.Start();

                foreach (ElementId panelId in arPanelIds)
                {
                    int panelIdInt = panelId.IntegerValue;
                    try
                    {
                        Element element = doc.GetElement(panelId);
                        if (element == null || !element.IsValidObject)
                        {
                            _arSkippedInvalid++;
                            _logger.Info($"{tag} SKIP (invalid object). PanelId={panelIdInt}");
                            continue;
                        }

                        if (element.GetTypeId() == targetTypeId)
                        {
                            _arSkippedKrType++;
                            continue;
                        }

                        double offsetFt = 0.0;
                        Parameter arOffsetParam = element.get_Parameter(BuiltInParameter.WALL_LOCATION_LINE_OFFSET_PARAM);
                        if (arOffsetParam != null && arOffsetParam.StorageType == StorageType.Double)
                            offsetFt = arOffsetParam.AsDouble();

                        bool isWallPanel = element is Wall;
                        int materialIdInt = -1;
                        Element arType = doc.GetElement(element.GetTypeId());
                        Parameter arMatParam = arType?.LookupParameter("Материал несущих конструкций");
                        if (arMatParam != null && arMatParam.StorageType == StorageType.ElementId)
                            materialIdInt = arMatParam.AsElementId().IntegerValue;
                        if (materialIdInt <= 0)
                        {
                            Parameter matParam = arType?.LookupParameter("Материал");
                            if (matParam != null && matParam.StorageType == StorageType.ElementId)
                                materialIdInt = matParam.AsElementId().IntegerValue;
                        }

                        if (!isWallPanel && Math.Abs(offsetFt) < EPS)
                        {
                            FamilyInstance fi = element as FamilyInstance;
                            Wall hostCw = fi?.Host as Wall;
                            if (hostCw != null)
                            {
                                LocationCurve lc = hostCw.Location as LocationCurve;
                                if (lc != null)
                                {
                                    XYZ wallNormal = hostCw.Orientation.Normalize();
                                    XYZ wallPt = lc.Curve.Evaluate(0.5, true);
                                    BoundingBoxXYZ pbb = element.get_BoundingBox(null);
                                    if (pbb != null)
                                    {
                                        XYZ panelCenter = (pbb.Min + pbb.Max) / 2.0;
                                        offsetFt = Math.Abs((panelCenter - wallPt).DotProduct(wallNormal));
                                        _logger.Info($"{tag} [FI-GEO] Id={panelIdInt} geoOffsetMm={offsetFt * FEET_TO_MM:F1}");
                                    }
                                }
                            }
                        }

                        if (isWallPanel)
                        {
                            BoundingBoxXYZ preBb = element.get_BoundingBox(null);
                            if (preBb != null)
                            {
                                XYZ wallNormal = XYZ.BasisY;
                                int cwIdInt = -1;
                                if (panelToCwInfo.TryGetValue(panelId, out var cwInfo))
                                {
                                    wallNormal = cwInfo.normal;
                                    cwIdInt = cwInfo.cwIdInt;
                                }
                                wallPendingOffsets.Add((preBb.Min, preBb.Max, offsetFt, wallNormal, cwIdInt, materialIdInt));
                                _logger.Info($"{tag} [AR-Wall] Id={panelIdInt} cwId={cwIdInt} offsetMm={offsetFt * FEET_TO_MM:F0} matId={materialIdInt} bb=({preBb.Min.X:F3},{preBb.Min.Z:F3})..({preBb.Max.X:F3},{preBb.Max.Z:F3})");
                            }
                        }

                        bool wasPinned = element.Pinned;
                        if (wasPinned) element.Pinned = false;
                        element.ChangeTypeId(targetTypeId);
                        if (wasPinned && element.IsValidObject) element.Pinned = true;

                        if (!isWallPanel)
                        {
                            Element krElem = doc.GetElement(panelId);
                            if (krElem != null && krElem.IsValidObject)
                                TransferKrParameters(krElem, offsetFt, materialIdInt, doc, $"{tag}[TX1]",
                                    ref _arOffsetsOk, ref _arOffsetsFail,
                                    ref _arMatsOk,   ref _arMatsFail,
                                    ref _arMat2Ok,   ref _arMat2Fail);
                        }

                        _arReplaced++;
                    }
                    catch (Autodesk.Revit.Exceptions.InvalidObjectException)
                    {
                        _arSkippedInvalid++;
                        _logger.Info($"{tag} SKIP (InvalidObjectException). PanelId={panelIdInt}");
                    }
                    catch (Exception ex)
                    {
                        _arFailed++;
                        _logger.Info($"{tag} FAILED. PanelId={panelIdInt}. Error: {ex.Message}");
                    }
                }

                tx.Commit();
            }

            return wallPendingOffsets;
        }

        private void ExecuteTx2_TransferOffsets(Document doc, string tag,
            List<(XYZ bbMin, XYZ bbMax, double offsetFt, XYZ wallNormal, int cwIdInt, int materialIdInt)> wallPendingOffsets)
        {
            const string TARGET_FAMILY = REGULAR_PANEL_FAMILY_NAME;
            const string TARGET_TYPE   = REGULAR_PANEL_FAMILY_NAME_TYPE;
            const double MIN_OVERLAP   = 0.5;

            var krFiPanels = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .OfCategory(BuiltInCategory.OST_CurtainWallPanels)
                .Cast<FamilyInstance>()
                .Where(fi => {
                    string fam = fi.Symbol?.Family?.Name ?? string.Empty;
                    return string.Equals(fam, TARGET_FAMILY, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(fi.Symbol.Name, TARGET_TYPE, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            var krByCw = krFiPanels
                .GroupBy(fi => fi.Host?.Id.IntegerValue ?? -1)
                .ToDictionary(g => g.Key, g => g.ToList());

            _logger.Info($"{tag} TX2: wallPending={wallPendingOffsets.Count}, krFiPool={krFiPanels.Count}, cwGroups={krByCw.Count}");
            foreach (var kvp in krByCw.OrderByDescending(k => k.Value.Count).Take(10))
                _logger.Info($"{tag} TX2: cwId={kvp.Key} krCount={kvp.Value.Count}");

            using (Transaction tx2 = new Transaction(doc, "Transfer AR offsets to KR panels"))
            {
                tx2.Start();

                var matchedKrIds = new HashSet<int>();
                int noCwGroupCount = 0;

                foreach (var (bbMin, bbMax, offsetFt, wallNormal, cwIdInt, materialIdInt) in wallPendingOffsets)
                {
                    List<FamilyInstance> candidates;
                    bool usedFallback = false;
                    if (krByCw.TryGetValue(cwIdInt, out var cwCandidates))
                        candidates = cwCandidates;
                    else
                    {
                        candidates = krFiPanels;
                        usedFallback = true;
                        noCwGroupCount++;
                        _logger.Info($"{tag} [TX2-WARN] cwId={cwIdInt} not in krByCw, using full pool");
                    }

                    XYZ xVec = new XYZ(-wallNormal.Y, wallNormal.X, 0).Normalize();
                    double arH1 = bbMin.DotProduct(xVec), arH2 = bbMax.DotProduct(xVec);
                    double arHmin = Math.Min(arH1, arH2), arHmax = Math.Max(arH1, arH2);
                    double arVmin = bbMin.Z, arVmax = bbMax.Z;
                    double arArea = (arHmax - arHmin) * (arVmax - arVmin);

                    FamilyInstance best = null;
                    double bestOverlap = 0;

                    foreach (var fi in candidates)
                    {
                        if (matchedKrIds.Contains(fi.Id.IntegerValue)) continue;
                        BoundingBoxXYZ fiBb = fi.get_BoundingBox(null);
                        if (fiBb == null) continue;
                        double fiH1 = fiBb.Min.DotProduct(xVec), fiH2 = fiBb.Max.DotProduct(xVec);
                        double fiHmin = Math.Min(fiH1, fiH2), fiHmax = Math.Max(fiH1, fiH2);
                        double fiVmin = fiBb.Min.Z, fiVmax = fiBb.Max.Z;
                        double overlapH = Math.Max(0, Math.Min(arHmax, fiHmax) - Math.Max(arHmin, fiHmin));
                        double overlapV = Math.Max(0, Math.Min(arVmax, fiVmax) - Math.Max(arVmin, fiVmin));
                        double overlapFrac = arArea > 0 ? (overlapH * overlapV) / arArea : 0;
                        if (overlapFrac > bestOverlap) { bestOverlap = overlapFrac; best = fi; }
                    }

                    _logger.Info($"{tag} [TX2] cwId={cwIdInt} candidates={candidates.Count} offsetMm={offsetFt * FEET_TO_MM:F0} → bestId={best?.Id.IntegerValue} overlap={bestOverlap:P0}{(usedFallback ? " FALLBACK" : "")}");

                    if (best != null && bestOverlap >= MIN_OVERLAP)
                    {
                        matchedKrIds.Add(best.Id.IntegerValue);

                        if (_selMode == PluginSelectionMode.ByPanels && _selectedPanelIds != null)
                            _selectedPanelIds.Add(new ElementId(best.Id.IntegerValue));

                        TransferKrParameters(best, offsetFt, materialIdInt, doc, $"{tag}[TX2]",
                            ref _arOffsetsOk, ref _arOffsetsFail,
                            ref _arMatsOk,   ref _arMatsFail,
                            ref _arMat2Ok,   ref _arMat2Fail);

                        _logger.Info($"{tag} [MATCH] KRFIId={best.Id.IntegerValue} cwId={cwIdInt} offsetMm={offsetFt * FEET_TO_MM:F0} overlap={bestOverlap:P0} ✓");
                    }
                    else
                    {
                        _arOffsetsFail++;
                        _logger.Info($"{tag} [NOMATCH] cwId={cwIdInt} bestOverlap={bestOverlap:P0} < {MIN_OVERLAP:P0}");
                    }
                }

                _logger.Info($"{tag} [TX2-SUMMARY] matched={matchedKrIds.Count} noMatch={_arOffsetsFail} noCwGroup={noCwGroupCount}");
                tx2.Commit();
            }
        }
    }
}
