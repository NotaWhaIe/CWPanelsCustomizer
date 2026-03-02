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
    public class CurtainWallPanelDto
    {
        public ElementId Id { get; set; }
        public FamilyInstance PanelElement { get; set; }
        public BoundingBoxXYZ WorldBoundingBox { get; set; }
        public BoundingBoxXYZ LocalBoundingBox { get; set; }

        public bool IsMirrored { get; set; }
        public PanelSideRelativeToOpening SideRelativeToOpening { get; set; }
            = PanelSideRelativeToOpening.Undefined;
        public double? DxFromOpeningCenterFt { get; set; }
    }

    public class OpeningModelDto
    {
        public ElementId Id { get; set; }
        public FamilyInstance OpeningElement { get; set; }
        public BoundingBoxXYZ WorldBoundingBox { get; set; }
        public BoundingBoxXYZ LocalBoundingBox { get; set; }
    }

    public class CurtainWallDataDto
    {
        public ElementId Id { get; set; }
        public Wall CurtainWallElement { get; set; }
        public Transform InverseTransform { get; set; }
        public List<OpeningModelDto> IntersectingOpenings { get; set; } = new List<OpeningModelDto>();
        public List<CurtainWallPanelDto> Panels { get; set; } = new List<CurtainWallPanelDto>();
    }
    public enum PanelSideRelativeToOpening
    {
        Undefined = 0, // не анализировали или не пересекается
        OnAxis = 1,    // на оси окна (в пределах допуска)
        Left = 2,
        Right = 3
    }

    [Transaction(TransactionMode.Manual)]
    public class CurtainPanelWindowConfiguration : IExternalCommand
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

        // --- Авто-отмена предыдущего запуска ---
        // [(newKrFiId, origArTypeId)]: сбрасывает смещение и откатывает тип AR→KR
        private static readonly List<(int krFiId, int origArTypeId)> _undoRecord
            = new List<(int, int)>();


        private const double EPS = 1e-9;
        private const double FEET_TO_MM = 304.8;


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

        /// <summary>Откат предыдущего запуска: сброс смещения + возврат типа AR.</summary>
        private void UndoPreviousRun(Document doc, string tag)
        {
            if (_undoRecord.Count == 0) return;
            _logger.Info($"{tag} [UNDO] Откат предыдущего запуска: {_undoRecord.Count} панелей");
            using (Transaction tx = new Transaction(doc, "Откат AR→KR (debug)"))
            {
                tx.Start();
                int ok = 0, skip = 0;
                foreach (var (krFiId, origArTypeId) in _undoRecord)
                {
                    try
                    {
                        Element e = doc.GetElement(new ElementId(krFiId));
                        if (e == null || !e.IsValidObject) { skip++; continue; }
                        Parameter p = e.LookupParameter("Смещение от плоскости фасада");
                        if (p != null && !p.IsReadOnly) p.Set(0.0);
                        if (origArTypeId > 0) e.ChangeTypeId(new ElementId(origArTypeId));
                        ok++;
                    }
                    catch { skip++; }
                }
                tx.Commit();
                _logger.Info($"{tag} [UNDO] ok={ok} skip={skip}");
            }
            _undoRecord.Clear();
        }

        private void ReplaceArCurtainPanelsWithKrPanels(Document doc)
        {
            const string TAG = "[ReplaceArCurtainPanelsWithKrPanels]";
            const string KR_OFFSET_PARAM = "Смещение от плоскости фасада";

            // 0) Откат предыдущего запуска
            UndoPreviousRun(doc, TAG);

            // Целевой КР-тип
            const string TARGET_KR_PANEL_FAMILY_NAME = REGULAR_PANEL_FAMILY_NAME;
            const string TARGET_KR_PANEL_TYPE_NAME = REGULAR_PANEL_FAMILY_NAME_TYPE;

            // Критерий "АР-панели":
            // AP_ / АР   — стандартные АР-типы
            // Кассета     — системные панели (Системная панель / Кассета_RAL 5005)
            const string AR_TYPE_PREFIX_1 = "AP_";
            const string AR_TYPE_PREFIX_2 = "АР";
            const string AR_TYPE_PREFIX_3 = "Кассета";

            if (doc == null)
                throw new ArgumentNullException(nameof(doc));

            // 1a) Wall-панели витража
            // 1b) FamilyInstance-панели витража (напр. "Системная панель / Кассета_RAL 5005")
            List<ElementId> wallPanelIds;
            List<ElementId> fiPanelIds;

            // Собираем панели через тот же коллектор что Mode=All — стабильный источник
            var allWallPanels = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_CurtainWallPanels)
                .WhereElementIsNotElementType()
                .OfType<Wall>()
                .ToList();

            var allFiPanels = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_CurtainWallPanels)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .ToList();

            if (_selMode == PluginSelectionMode.ByWalls && _selectedWallIds != null && _selectedWallIds.Count > 0)
            {
                // FI-панели: фильтруем по Host (родительский витраж)
                fiPanelIds = allFiPanels
                    .Where(fi => fi.Host != null && _selectedWallIds.Contains(fi.Host.Id))
                    .Select(fi => fi.Id)
                    .ToList();

                // Wall-панели: через GetDependentElements, т.к. GetPanelIds() их не возвращает
                var wallPanelSet = new HashSet<ElementId>();
                foreach (ElementId wallId in _selectedWallIds)
                {
                    Wall cw = doc.GetElement(wallId) as Wall;
                    if (cw == null) continue;
                    // GetDependentElements(Wall) включает Wall-панели витража
                    foreach (ElementId depId in cw.GetDependentElements(new ElementClassFilter(typeof(Wall))))
                        wallPanelSet.Add(depId);
                }
                // Пересекаем с allWallPanels (уже отфильтровано: OST_CurtainWallPanels)
                wallPanelIds = allWallPanels
                    .Where(w => wallPanelSet.Contains(w.Id))
                    .Select(w => w.Id)
                    .ToList();
            }
            else
            {
                wallPanelIds = allWallPanels.Select(w => w.Id).ToList();
                fiPanelIds = allFiPanels.Select(fi => fi.Id).ToList();
            }

            _logger.Info($"{TAG} Found Wall panels: {wallPanelIds.Count}, FamilyInstance panels: {fiPanelIds.Count}");

            // 2) Отбираем ТОЛЬКО АР панели (вне транзакции)
            List<ElementId> arPanelIds = new List<ElementId>();
            int skippedNotAr = 0;
            int skippedInvalidAtScan = 0;

            // 2a) Проход по Wall-панелям
            foreach (ElementId id in wallPanelIds)
            {
                Element e = doc.GetElement(id);
                if (e == null || !e.IsValidObject) { skippedInvalidAtScan++; continue; }

                Wall w = e as Wall;
                if (w == null) { skippedInvalidAtScan++; continue; }

                ElementType t = doc.GetElement(w.GetTypeId()) as ElementType;
                if (t == null) { skippedInvalidAtScan++; continue; }

                string typeName = t.Name ?? string.Empty;

                bool isAr =
                    typeName.StartsWith(AR_TYPE_PREFIX_1, StringComparison.OrdinalIgnoreCase) ||
                    typeName.StartsWith(AR_TYPE_PREFIX_2, StringComparison.OrdinalIgnoreCase) ||
                    typeName.StartsWith(AR_TYPE_PREFIX_3, StringComparison.OrdinalIgnoreCase);

                if (isAr)
                    arPanelIds.Add(id);
                else
                    skippedNotAr++;
            }

            // 2b) Проход по FamilyInstance-панелям
            // Критерий АР для FI: семейство "Системная панель" (загруженное, а не встроенное)
            const string AR_FI_FAMILY = "Системная панель";
            foreach (ElementId id in fiPanelIds)
            {
                Element e = doc.GetElement(id);
                if (e == null || !e.IsValidObject) { skippedInvalidAtScan++; continue; }

                FamilyInstance fi = e as FamilyInstance;
                if (fi == null) { skippedInvalidAtScan++; continue; }

                string famName  = fi.Symbol?.Family?.Name ?? string.Empty;
                string typeName = fi.Symbol?.Name ?? string.Empty;

                // "Системная панель" + тип "Кассета_*" — АР-панель для замены.
                // "Системная панель" + тип "Стена" — стекло/рамка, не трогаем.
                bool isAr = string.Equals(famName, AR_FI_FAMILY, StringComparison.OrdinalIgnoreCase)
                         && typeName.StartsWith(AR_TYPE_PREFIX_3, StringComparison.OrdinalIgnoreCase);

                if (isAr)
                    arPanelIds.Add(id);
                else
                {
                    skippedNotAr++;
                }
            }

            _logger.Info($"{TAG} AR panels total: {arPanelIds.Count} (skippedNotAr={skippedNotAr}, skippedInvalid={skippedInvalidAtScan})");

            // Режим ByPanels: оставляем только выделенные
            if (_selMode == PluginSelectionMode.ByPanels && _selectedPanelIds != null)
            {
                int before = arPanelIds.Count;
                arPanelIds = arPanelIds.Where(id => _selectedPanelIds.Contains(id)).ToList();
                _logger.Info($"{TAG} ByPanels filter: {before} → {arPanelIds.Count} AR panels");
            }

            // Если АР панелей нет — логируем диагностику и выходим
            if (arPanelIds.Count == 0)
            {
                // Диагностика: какие семейства/типы есть в области поиска
                var sampleFamilies = allFiPanels
                    .Where(fi => _selMode != PluginSelectionMode.ByWalls ||
                                 (_selectedWallIds != null && fi.Host != null && _selectedWallIds.Contains(fi.Host.Id)))
                    .GroupBy(fi => $"{fi.Symbol?.FamilyName}/{fi.Symbol?.Name}")
                    .OrderByDescending(g => g.Count())
                    .Take(5)
                    .Select(g => $"{g.Key} ×{g.Count()}");
                _logger.Info($"{TAG} No AR panels. Top families in scope: {string.Join("; ", sampleFamilies)}");
                return;
            }

            // 3) Находим СИМВОЛ (FamilySymbol) нужного КР семейства и нужного типа
            FamilySymbol targetSymbol = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(fs =>
                {
                    Category c = fs.Category;
                    if (c == null)
                    {
                        return false;
                    }

                    if (c.Id.IntegerValue != (int)BuiltInCategory.OST_CurtainWallPanels)
                    {
                        return false;
                    }

                    if (fs.Family == null)
                    {
                        return false;
                    }

                    bool familyMatch = string.Equals(fs.Family.Name, TARGET_KR_PANEL_FAMILY_NAME, StringComparison.OrdinalIgnoreCase);
                    if (!familyMatch)
                    {
                        return false;
                    }

                    bool typeMatch = string.Equals(fs.Name, TARGET_KR_PANEL_TYPE_NAME, StringComparison.OrdinalIgnoreCase);
                    return typeMatch;
                });

            if (targetSymbol == null)
            {
                throw new InvalidOperationException(
                    $"{TAG} Target KR panel type not found. " +
                    $"Expected Family='{TARGET_KR_PANEL_FAMILY_NAME}', Type='{TARGET_KR_PANEL_TYPE_NAME}' in OST_CurtainWallPanels.");
            }

            _logger.Info($"{TAG} Target symbol: Family='{targetSymbol.Family.Name}', Type='{targetSymbol.Name}', Id={targetSymbol.Id.IntegerValue}");

            // 4) Активируем символ (безопасно)
            if (!targetSymbol.IsActive)
            {
                using (Transaction tx = new Transaction(doc, "Activate target KR panel type"))
                {
                    tx.Start();
                    targetSymbol.Activate();
                    tx.Commit();
                }
            }

            ElementId targetTypeId = targetSymbol.Id;

            int replaced = 0;
            int skippedAlreadyKrType = 0;
            int skippedInvalid = 0;
            int failed = 0;
            int offsetsTransferred = 0;
            int offsetsFailed = 0;
            int materialsTransferred = 0;
            int materialsFailed = 0;

            const string KR_COLOR_PARAM = "Цвет по шкале RAL (н/в)";
            const string KR_ANGLE_PARAM = "Угол_Слева";

            // Маппинг Wall-панелей витража → (нормаль, Id витражной стены).
            // Нужен для TX2: проекция на плоскость стены + scoping по витражу.
            var panelToCwInfo = new Dictionary<ElementId, (XYZ normal, int cwIdInt)>();
            {
                var allWallPanelIds = new HashSet<ElementId>(allWallPanels.Select(w => w.Id));
                var curtainWalls = new FilteredElementCollector(doc)
                    .OfClass(typeof(Wall)).Cast<Wall>()
                    .Where(w => w.CurtainGrid != null).ToList();
                foreach (var cw in curtainWalls)
                {
                    XYZ n = (cw.Orientation ?? XYZ.BasisY).Normalize();
                    int cwId = cw.Id.IntegerValue;
                    foreach (var depId in cw.GetDependentElements(new ElementClassFilter(typeof(Wall))))
                        if (allWallPanelIds.Contains(depId))
                            panelToCwInfo[depId] = (n, cwId);
                }
                _logger.Info($"{TAG} panelToCwInfo built: {panelToCwInfo.Count} Wall panels mapped");
            }

            // Wall→FI замены: после ChangeTypeId старый ID инвалидируется (Revit создаёт новый элемент).
            // Сохраняем BB (Min/Max) до замены; после TX1 матчим по перекрытию BoundingBox в плоскости стены.
            // BB-overlap не зависит от систематического смещения центров (~16мм) — нужна только > 50% площадь.
            var wallPendingOffsets = new List<(XYZ bbMin, XYZ bbMax, double offsetFt, int origArTypeId, XYZ wallNormal, int cwIdInt, int materialIdInt)>();

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
                            skippedInvalid++;
                            _logger.Info($"{TAG} SKIP (invalid object). PanelId={panelIdInt}");
                            continue;
                        }

                        // Уже нужный КР-тип — не трогаем (работает для Wall и FamilyInstance)
                        if (element.GetTypeId() == targetTypeId)
                        {
                            skippedAlreadyKrType++;
                            continue;
                        }

                        // Читаем смещение из АР-панели до замены типа
                        double offsetFt = 0.0;
                        Parameter arOffsetParam = element.get_Parameter(BuiltInParameter.WALL_LOCATION_LINE_OFFSET_PARAM);
                        if (arOffsetParam != null && arOffsetParam.StorageType == StorageType.Double)
                            offsetFt = arOffsetParam.AsDouble();

                        bool isWallPanel = element is Wall;
                        int origArTypeId = element.GetTypeId().IntegerValue;

                        // Читаем MaterialId с AR-панели до замены типа
                        int materialIdInt = -1;
                        {
                            Parameter arMatParam = element.LookupParameter("Материал несущих конструкций");
                            if (arMatParam != null && arMatParam.StorageType == StorageType.ElementId)
                                materialIdInt = arMatParam.AsElementId().IntegerValue;
                        }

                        // FI-панели: WALL_LOCATION_LINE_OFFSET_PARAM не существует на FamilyInstance
                        // → геометрический расчёт offset от плоскости витражной стены
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
                                        _logger.Info($"{TAG} [FI-GEO] Id={panelIdInt} geoOffsetMm={offsetFt * FEET_TO_MM:F1}");
                                    }
                                }
                            }
                        }

                        if (isWallPanel)
                        {
                            // Сохраняем BB + нормаль + cwId + materialId для TX2-матчинга
                            // Включаем ВСЕ Wall, в т.ч. с нулевым offset — материал нужно перенести всегда
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
                                wallPendingOffsets.Add((preBb.Min, preBb.Max, offsetFt, origArTypeId, wallNormal, cwIdInt, materialIdInt));
                                _logger.Info($"{TAG} [AR-Wall] Id={panelIdInt} cwId={cwIdInt} offsetMm={offsetFt * FEET_TO_MM:F0} matId={materialIdInt} bb=({preBb.Min.X:F3},{preBb.Min.Z:F3})..({preBb.Max.X:F3},{preBb.Max.Z:F3})");
                            }
                        }

                        bool wasPinned = element.Pinned;
                        if (wasPinned) element.Pinned = false;

                        // ChangeTypeId работает и для Wall, и для FamilyInstance панелей витража
                        element.ChangeTypeId(targetTypeId);

                        if (wasPinned && element.IsValidObject) element.Pinned = true;

                        // FI-панели: ID остаётся прежним после смены типа — переносим смещение сразу
                        if (!isWallPanel)
                        {
                            // Записываем в undo даже при нулевом смещении (для отката типа)
                            _undoRecord.Add((panelId.IntegerValue, origArTypeId));

                            // Угол_Слева = 0 — инициализация перед прочими параметрами
                            Element krElemAngle = doc.GetElement(panelId);
                            Parameter krAngleP = krElemAngle?.LookupParameter(KR_ANGLE_PARAM);
                            if (krAngleP != null && !krAngleP.IsReadOnly)
                                krAngleP.Set(0.0);
                        }
                        if (!isWallPanel && Math.Abs(offsetFt) >= EPS)
                        {
                            Element krElem = doc.GetElement(panelId);
                            Parameter krOffsetParam = krElem?.LookupParameter(KR_OFFSET_PARAM);
                            if (krOffsetParam != null && !krOffsetParam.IsReadOnly)
                            {
                                krOffsetParam.Set(offsetFt);
                                offsetsTransferred++;
                                _logger.Info($"{TAG} [FI-transferred] Id={panelIdInt} offsetMm={offsetFt * FEET_TO_MM:F0}");
                            }
                            else
                            {
                                offsetsFailed++;
                                _logger.Info($"{TAG} Offset not transferred (FI). PanelId={panelIdInt}, offsetFt={offsetFt:F6}, paramFound={krOffsetParam != null}");
                            }
                        }
                        // FI-панели: перенос материала сразу (ID не меняется)
                        if (!isWallPanel && materialIdInt > 0)
                        {
                            Element krElem = doc.GetElement(panelId);
                            Material mat = doc.GetElement(new ElementId(materialIdInt)) as Material;
                            Parameter krColorP = krElem?.LookupParameter(KR_COLOR_PARAM);
                            if (mat != null && krColorP != null && !krColorP.IsReadOnly && krColorP.StorageType == StorageType.String)
                            {
                                krColorP.Set(mat.Name);
                                materialsTransferred++;
                                _logger.Info($"{TAG} [FI-MAT] Id={panelIdInt} matId={materialIdInt} mat='{mat.Name}' ok");
                            }
                            else
                            {
                                materialsFailed++;
                                _logger.Info($"{TAG} [FI-MAT-FAIL] Id={panelIdInt} matId={materialIdInt} paramFound={krColorP != null} mat={mat != null}");
                            }
                        }

                        replaced++;
                    }
                    catch (Autodesk.Revit.Exceptions.InvalidObjectException)
                    {
                        skippedInvalid++;
                        _logger.Info($"{TAG} SKIP (InvalidObjectException). PanelId={panelIdInt}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        _logger.Info($"{TAG} FAILED. PanelId={panelIdInt}. Error: {ex.Message}");
                    }
                }

                tx.Commit();
            }

            // Транзакция 2: перенос смещений для Wall→FI панелей.
            // BoundingBox новых FI становится доступен только ПОСЛЕ commit TX1.
            // Матчинг по проекции на плоскость стены: убираем глубинную компоненту (offset-параметр
            // смещает BB по нормали стены, XZ-позиция совпадает с точностью ~15 мм).
            if (wallPendingOffsets.Count > 0)
            {
                var krFiPanels = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .OfCategory(BuiltInCategory.OST_CurtainWallPanels)
                    .Cast<FamilyInstance>()
                    .Where(fi => {
                        string fam = fi.Symbol?.Family?.Name ?? string.Empty;
                        return string.Equals(fam, TARGET_KR_PANEL_FAMILY_NAME, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(fi.Symbol.Name, TARGET_KR_PANEL_TYPE_NAME, StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                // Группировка KR-панелей по витражной стене (Host)
                var krByCw = krFiPanels
                    .GroupBy(fi => fi.Host?.Id.IntegerValue ?? -1)
                    .ToDictionary(g => g.Key, g => g.ToList());

                _logger.Info($"{TAG} TX2: wallPending={wallPendingOffsets.Count}, krFiPool={krFiPanels.Count}, cwGroups={krByCw.Count}");
                foreach (var kvp in krByCw.OrderByDescending(k => k.Value.Count).Take(10))
                    _logger.Info($"{TAG} TX2: cwId={kvp.Key} krCount={kvp.Value.Count}");

                using (Transaction tx2 = new Transaction(doc, "Transfer AR offsets to KR panels"))
                {
                    tx2.Start();

                    // Матчинг по перекрытию BoundingBox в плоскости стены.
                    // Scoped: кандидаты только из того же витража (cwIdInt).
                    // Dedup: matchedKrIds исключает повторное назначение.
                    const double MIN_OVERLAP = 0.5;
                    var matchedKrIds = new HashSet<int>();
                    int noCwGroupCount = 0;

                    foreach (var (bbMin, bbMax, offsetFt, origArTypeId, wallNormal, cwIdInt, materialIdInt) in wallPendingOffsets)
                    {
                        // Выбираем кандидатов: только из того же витража
                        List<FamilyInstance> candidates;
                        bool usedFallback = false;
                        if (krByCw.TryGetValue(cwIdInt, out var cwCandidates))
                        {
                            candidates = cwCandidates;
                        }
                        else
                        {
                            // Fallback: не нашли группу — используем весь пул (не должно случаться)
                            candidates = krFiPanels;
                            usedFallback = true;
                            noCwGroupCount++;
                            _logger.Info($"{TAG} [TX2-WARN] cwId={cwIdInt} not in krByCw, using full pool");
                        }

                        // Горизонтальная ось в плоскости стены
                        XYZ xVec = new XYZ(-wallNormal.Y, wallNormal.X, 0).Normalize();

                        // Проекция AR BB на плоскость стены (горизонталь + высота Z)
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

                        _logger.Info($"{TAG} [TX2] cwId={cwIdInt} candidates={candidates.Count} offsetMm={offsetFt * FEET_TO_MM:F0} → bestId={best?.Id.IntegerValue} overlap={bestOverlap:P0}{(usedFallback ? " FALLBACK" : "")}");

                        if (best != null && bestOverlap >= MIN_OVERLAP)
                        {
                            matchedKrIds.Add(best.Id.IntegerValue);
                            _undoRecord.Add((best.Id.IntegerValue, origArTypeId));

                            // Угол_Слева = 0 — инициализация перед прочими параметрами
                            Parameter krAngleP = best.LookupParameter(KR_ANGLE_PARAM);
                            if (krAngleP != null && !krAngleP.IsReadOnly)
                            {
                                krAngleP.Set(0.0);
                                _logger.Info($"{TAG} [ANGLE] KRFIId={best.Id.IntegerValue} Угол_Слева=0 ok");
                            }
                            else
                                _logger.Info($"{TAG} [ANGLE-WARN] KRFIId={best.Id.IntegerValue} param not found/readonly");

                            // Перенос offset
                            if (Math.Abs(offsetFt) >= EPS)
                            {
                                Parameter krP = best.LookupParameter(KR_OFFSET_PARAM);
                                if (krP != null && !krP.IsReadOnly)
                                {
                                    krP.Set(offsetFt);
                                    offsetsTransferred++;
                                }
                                else
                                {
                                    offsetsFailed++;
                                    _logger.Info($"{TAG} [FAIL-param] KRFIId={best.Id.IntegerValue} paramFound={krP != null}");
                                }
                            }

                            // Перенос материала → "Цвет по шкале RAL (н/в)"
                            if (materialIdInt > 0)
                            {
                                Material mat = doc.GetElement(new ElementId(materialIdInt)) as Material;
                                Parameter krColorP = best.LookupParameter(KR_COLOR_PARAM);
                                if (mat != null && krColorP != null && !krColorP.IsReadOnly && krColorP.StorageType == StorageType.String)
                                {
                                    krColorP.Set(mat.Name);
                                    materialsTransferred++;
                                    _logger.Info($"{TAG} [TX2-MAT] KRFIId={best.Id.IntegerValue} matId={materialIdInt} ok");
                                }
                                else
                                {
                                    materialsFailed++;
                                    _logger.Info($"{TAG} [TX2-MAT-FAIL] KRFIId={best.Id.IntegerValue} matId={materialIdInt} paramFound={krColorP != null} mat={mat != null}");
                                }
                            }

                            _logger.Info($"{TAG} [MATCH] KRFIId={best.Id.IntegerValue} cwId={cwIdInt} offsetMm={offsetFt * FEET_TO_MM:F0} overlap={bestOverlap:P0} ✓");
                        }
                        else
                        {
                            offsetsFailed++;
                            _logger.Info($"{TAG} [NOMATCH] cwId={cwIdInt} bestOverlap={bestOverlap:P0} < {MIN_OVERLAP:P0}");
                        }
                    }

                    _logger.Info($"{TAG} [TX2-SUMMARY] matched={matchedKrIds.Count} noMatch={offsetsFailed} noCwGroup={noCwGroupCount}");

                    tx2.Commit();
                }
            }

            _logger.Info($"{TAG} SUMMARY:");
            _logger.Info($"{TAG}  AR panels processed: {arPanelIds.Count}");
            _logger.Info($"{TAG}  Replaced (AR->KR): {replaced}");
            _logger.Info($"{TAG}  Offsets transferred: {offsetsTransferred}, failed: {offsetsFailed}");
            _logger.Info($"{TAG}  Materials transferred: {materialsTransferred}, failed: {materialsFailed}");
            _logger.Info($"{TAG}  Skipped (already KR type): {skippedAlreadyKrType}");
            _logger.Info($"{TAG}  Skipped (invalid): {skippedInvalid}");
            _logger.Info($"{TAG}  Failed: {failed}");
        }


        // ==========================================================
        // === NEW FEATURE: MIRROR PANELS RIGHT OF OPENING (BY BB) ===
        // ==========================================================
        private void MirrorPanelsRightOfOpenings(List<CurtainWallDataDto> data)
        {
            // v6: Local X side detection (как в рабочем методе) + правила для L / Г_В2 / Рядовая_В3
            // + запись отладки в "Комментарий"

            const string TAG = "[MirrorPanelsRightOfOpenings v6_LocalRules_Debug]";
            const double SIDE_TOL_MM = 1.0;     // панели на оси окна не трогаем
            const double BAND_EXPAND_MM = 5.0;  // слегка расширяем bbox окна, чтобы увереннее ловить пересечение

            // Ключи типов панелей
            const string L_PANEL_KEY = L_PANEL_FAMILY_NAME;
            const string G_PANEL_KEY = G_PANEL_FAMILY_NAME;
            const string REG_PANEL_KEY = REGULAR_PANEL_FAMILY_NAME;

            double MmToFt(double mm) => mm / FEET_TO_MM;
            double sideTolFt = MmToFt(SIDE_TOL_MM);
            double bandExpandFt = MmToFt(BAND_EXPAND_MM);

            _logger.Info($"{TAG} START");

            if (data == null || data.Count == 0)
            {
                _logger.Info($"{TAG} data is null/empty -> END");
                return;
            }

            int wallsProcessed = 0;
            int openingsProcessed = 0;

            int panelsSeen = 0;
            int bbIntersectTypeMatched = 0;
            int needMirrorCandidates = 0;

            int flippedOk = 0;
            int skippedAlreadyProcessed = 0;
            int skippedNoFlip = 0;
            int flipErrors = 0;

            // чтобы не флипать одну и ту же панель несколько раз (если пересеклась с несколькими окнами)
            var processedPanels = new HashSet<ElementId>();

            BoundingBoxXYZ ExpandXZ(BoundingBoxXYZ b, double expandFt)
            {
                if (b == null) return null;
                return new BoundingBoxXYZ
                {
                    // расширяем по X и Z (как в твоём верном методе)
                    Min = new XYZ(b.Min.X - expandFt, b.Min.Y, b.Min.Z - expandFt),
                    Max = new XYZ(b.Max.X + expandFt, b.Max.Y, b.Max.Z + expandFt)
                };
            }

            string GetPanelTypeKey(FamilyInstance fi)
            {
                try
                {
                    var sym = fi?.Symbol;
                    if (sym == null) return string.Empty;

                    var typeName = sym.Name ?? string.Empty;
                    var famName = sym.FamilyName ?? string.Empty;

                    return $"{famName}::{typeName}";
                }
                catch { return string.Empty; }
            }

            bool KeyContains(string typeKey, string needle)
            {
                return !string.IsNullOrWhiteSpace(typeKey) &&
                       typeKey.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
            }

            bool TryFlipLikeSpacebar(FamilyInstance fi)
            {
                if (fi == null) return false;

                if (fi.CanFlipHand)
                {
                    fi.flipHand();
                    return true;
                }

                if (fi.CanFlipFacing)
                {
                    fi.flipFacing();
                    return true;
                }

                return false;
            }

            void SetCommentSafe(Element e, string text)
            {
                if (e == null) return;
                try
                {
                    var p = e.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (p == null || p.IsReadOnly) return;
                    p.Set(text);
                }
                catch
                {
                    // не валим транзакцию из-за комментария
                }
            }

            using (var t = new Transaction(_doc, "CW: Mirror panels by opening side (Local + Rules + Debug)"))
            {
                t.Start();
                //_doc.Regenerate();

                foreach (var cw in data)
                {
                    if (cw?.CurtainWallElement == null || cw.InverseTransform == null)
                        continue;

                    wallsProcessed++;
                    int wallId = cw.CurtainWallElement.Id.IntegerValue;

                    var openings = cw.IntersectingOpenings ?? new List<OpeningModelDto>();
                    var panels = cw.Panels ?? new List<CurtainWallPanelDto>();

                    _logger.Info($"{TAG} wallId={wallId} openings={openings.Count} panels={panels.Count}");

                    if (openings.Count == 0 || panels.Count == 0)
                        continue;

                    foreach (var opening in openings)
                    {
                        if (opening?.OpeningElement == null)
                            continue;

                        var obLocalFresh = GetLocalBBoxFresh(opening.OpeningElement, cw.InverseTransform);
                        if (obLocalFresh == null)
                        {
                            _logger.Info($"{TAG} wallId={wallId} openingId={opening.Id.IntegerValue} obLocal=null -> skip");
                            continue;
                        }

                        openingsProcessed++;
                        int opId = opening.OpeningElement.Id.IntegerValue;

                        var obLocal = ExpandXZ(obLocalFresh, bandExpandFt);
                        var wCenterX = CenterOf(obLocalFresh).X;

                        _logger.Info($"{TAG} wallId={wallId} openingId={opId} windowCenterX(local)={wCenterX:F4}");

                        foreach (var pdto in panels)
                        {
                            if (pdto?.PanelElement == null)
                                continue;

                            panelsSeen++;

                            var fi = pdto.PanelElement;

                            // не обрабатываем одну панель многократно (разные окна)
                            if (processedPanels.Contains(fi.Id))
                            {
                                skippedAlreadyProcessed++;
                                continue;
                            }

                            var pbLocal = GetLocalBBoxFresh(fi, cw.InverseTransform);
                            if (pbLocal == null)
                                continue;

                            // панель должна пересекаться с окном (в локале)
                            if (!Intersects3D(obLocal, pbLocal))
                                continue;

                            // фильтр по типу (участвуют только L, Г_В2, Рядовая_В3)
                            var typeKey = GetPanelTypeKey(fi);

                            bool isL = KeyContains(typeKey, L_PANEL_KEY);
                            bool isG = KeyContains(typeKey, G_PANEL_KEY);
                            bool isReg = KeyContains(typeKey, REG_PANEL_KEY);

                            if (!isL && !isG && !isReg)
                                continue;

                            bbIntersectTypeMatched++;

                            // положение панели относительно вертикальной оси окна (в ЛОКАЛЕ!)
                            var pCenterX = CenterOf(pbLocal).X;
                            double dx = pCenterX - wCenterX;

                            bool isRight = dx > sideTolFt;
                            bool isLeft = dx < -sideTolFt;
                            string sideText = isRight ? "СПРАВА" : (isLeft ? "СЛЕВА" : "НА ОСИ");

                            string panelTypeText = isL ? "L" : (isG ? "Г_В2" : "Рядовая_В3");

                            // По ТЗ: делаем действие только когда нужно зеркалить
                            bool needMirror = false;

                            // L: справа -> mirror, слева -> ничего
                            if (isL && isRight) needMirror = true;

                            // Г_В2: слева -> mirror, справа -> ничего
                            if (isG && isLeft) needMirror = true;

                            // Рядовая_В3: слева -> mirror, справа -> ничего
                            if (isReg && isLeft) needMirror = true;

                            // DTO как “источник истины” по намерению (по правилам)
                            pdto.IsMirrored = needMirror;

                            // Пишем отладку в "Комментарий"
                            string debug =
                                $"RW_DEBUG | WallId={wallId} | OpenId={opId} | PanelId={fi.Id.IntegerValue} | Type={panelTypeText} | " +
                                $"pCenterX={pCenterX:F4} | wCenterX={wCenterX:F4} | dx={dx:F4}ft | Side={sideText} | NeedMirror={(needMirror ? "YES" : "NO")}";

                            SetCommentSafe(fi, debug);

                            // На оси — не трогаем
                            if (!isRight && !isLeft)
                                continue;

                            // Если зеркалить не надо — выходим
                            if (!needMirror)
                                continue;

                            needMirrorCandidates++;

                            // теперь считаем панель обработанной, чтобы не флипнуть ещё раз на другом окне
                            processedPanels.Add(fi.Id);

                            try
                            {
                                bool flipped = TryFlipLikeSpacebar(fi);
                                if (!flipped)
                                {
                                    skippedNoFlip++;
                                    SetCommentSafe(fi, debug + " | RESULT=CANNOT_FLIP");
                                    continue;
                                }

                                //_doc.Regenerate();
                                flippedOk++;

                                bool afterMirrored = false;
                                bool afterHand = false, afterFacing = false;
                                try
                                {
                                    afterMirrored = fi.Mirrored;
                                    afterHand = fi.HandFlipped;
                                    afterFacing = fi.FacingFlipped;
                                }
                                catch { /* ignore */ }

                                SetCommentSafe(fi, debug + $" | RESULT=FLIPPED | After: Mirrored={afterMirrored} Hand={afterHand} Facing={afterFacing}");
                            }
                            catch (Exception ex)
                            {
                                flipErrors++;
                                _logger.Info($"{TAG} ERROR flip wallId={wallId} openingId={opId} panelId={fi.Id.IntegerValue}: {ex}");
                                SetCommentSafe(fi, debug + " | RESULT=ERROR");
                                // processedPanels.Add(fi.Id) уже стоит — чтобы не зациклиться на падающей панели
                            }
                        }
                    }
                }

                //_doc.Regenerate();
                t.Commit();
            }

            _logger.Info($"{TAG} END wallsProcessed={wallsProcessed} openingsProcessed={openingsProcessed}");
            _logger.Info($"{TAG} panelsSeen={panelsSeen}");
            _logger.Info($"{TAG} bbIntersectTypeMatched={bbIntersectTypeMatched}");
            _logger.Info($"{TAG} needMirrorCandidates={needMirrorCandidates}");
            _logger.Info($"{TAG} flippedOk={flippedOk}");
            _logger.Info($"{TAG} skippedAlreadyProcessed={skippedAlreadyProcessed}");
            _logger.Info($"{TAG} skippedNoFlip={skippedNoFlip}");
            _logger.Info($"{TAG} flipErrors={flipErrors}");
        }

        private void CalculateAndSetCutoutPanelsCuts(List<CurtainWallDataDto> data)
        {
            const string TAG = "[CalculateAndSetCutoutPanelsCuts_v3_BBox+Offsets]";

            const string CUTOUT_G_FAMILY = G_PANEL_FAMILY_NAME;
            const string CUTOUT_L_FAMILY = L_PANEL_FAMILY_NAME;

            const string CUT_PARAM_W = "Вырез_Ширина";
            const string CUT_PARAM_H = "Вырез_Высота";

            // Константы как в RegularPanelsCuts
            const double G_VERTICAL_MM = 35.0;
            const double G_HORIZONTAL_MM = 51.0;
            const double L_VERTICAL_MM = 77.0;
            const double L_HORIZONTAL_MM = 48.0;

            double MmToFt(double mm) => mm / FEET_TO_MM;

            _logger.Info($"{TAG} START");

            if (data == null || data.Count == 0)
            {
                _logger.Info($"{TAG} data is null/empty -> END");
                return;
            }

            // Пересечение BBox в ЛОКАЛЬНЫХ координатах стены: X=ширина, Z=высота
            bool TryGetBBoxIntersectionSizeXZ(BoundingBoxXYZ a, BoundingBoxXYZ b, out double widthFt, out double heightFt)
            {
                widthFt = 0.0;
                heightFt = 0.0;
                if (a == null || b == null) return false;

                double minX = Math.Max(a.Min.X, b.Min.X);
                double maxX = Math.Min(a.Max.X, b.Max.X);
                double ox = maxX - minX;

                double minZ = Math.Max(a.Min.Z, b.Min.Z);
                double maxZ = Math.Min(a.Max.Z, b.Max.Z);
                double oz = maxZ - minZ;

                if (ox <= EPS || oz <= EPS) return false;

                widthFt = ox;
                heightFt = oz;
                return true;
            }

            // Склейка ширины по стороне (как в исходном CalculateAndSetPanelCutout),
            // но безопасно для случаев, когда один из углов отсутствует.
            double CombineSideWidth(double wTop, double wBottom)
            {
                bool topOk = wTop > EPS;
                bool botOk = wBottom > EPS;

                if (topOk && botOk)
                    return Math.Abs(wBottom - wTop) / 2.0 + Math.Min(wBottom, wTop);

                if (topOk) return wTop;
                if (botOk) return wBottom;

                return 0.0;
            }

            // Выбор панели на угол: из кандидатов берём ближайшую к точке-углу окна (по XZ)
            FamilyInstance PickClosestByXZ(List<FamilyInstance> candidates, XYZ targetCornerXZ, Transform inv)
            {
                FamilyInstance best = null;
                double bestD2 = double.MaxValue;

                foreach (var fi in candidates)
                {
                    var bb = GetLocalBBoxFresh(fi, inv);
                    if (bb == null) continue;

                    var c = CenterOf(bb);
                    double dx = c.X - targetCornerXZ.X;
                    double dz = c.Z - targetCornerXZ.Z;
                    double d2 = dx * dx + dz * dz;

                    if (d2 < bestD2)
                    {
                        bestD2 = d2;
                        best = fi;
                    }
                }
                return best;
            }

            int wallsProcessed = 0;
            int openingsProcessed = 0;
            int cutoutsIntersectingTotal = 0;
            int cornersDetectedTotal = 0;
            int cutoutPanelsUpdated = 0;
            int paramsSet = 0;

            using (var t = new Transaction(_doc, "CW: Set cutout panel cuts by BBox (v3)"))
            {
                t.Start();
                //_doc.Regenerate();

                foreach (var cw in data)
                {
                    if (cw?.CurtainWallElement == null)
                        continue;

                    wallsProcessed++;
                    int wallId = cw.CurtainWallElement.Id.IntegerValue;

                    var openings = cw.IntersectingOpenings ?? new List<OpeningModelDto>();
                    var panelsAll = cw.Panels ?? new List<CurtainWallPanelDto>();

                    _logger.Info($"{TAG} wallId={wallId} openings={openings.Count} panels={panelsAll.Count}");

                    if (openings.Count == 0 || panelsAll.Count == 0)
                        continue;

                    // Все угловые кассеты (оба семейства)
                    var cutoutPanels = panelsAll
                        .Where(p => p?.PanelElement != null)
                        .Select(p => p.PanelElement)
                        .Where(fi =>
                        {
                            var fam = fi.Symbol?.Family?.Name ?? "";
                            return fam == CUTOUT_G_FAMILY || fam == CUTOUT_L_FAMILY;
                        })
                        .ToList();

                    _logger.Info($"{TAG} wallId={wallId} cutoutPanels={cutoutPanels.Count}");
                    if (cutoutPanels.Count == 0)
                        continue;

                    foreach (var op in openings)
                    {
                        if (op?.OpeningElement == null)
                            continue;

                        var opBox = GetLocalBBoxFresh(op.OpeningElement, cw.InverseTransform);
                        if (opBox == null)
                        {
                            _logger.Info($"{TAG} wallId={wallId} openingId={op.Id.IntegerValue} opBox=null -> skip");
                            continue;
                        }

                        openingsProcessed++;
                        int opId = op.OpeningElement.Id.IntegerValue;
                        var opCenter = CenterOf(opBox);

                        // Кандидаты: угловые панели, которые пересекаются с окном
                        var intersectingCutouts = new List<FamilyInstance>();
                        foreach (var fi in cutoutPanels)
                        {
                            var pBox = GetLocalBBoxFresh(fi, cw.InverseTransform);
                            if (pBox == null) continue;

                            if (Intersects3D(opBox, pBox))
                                intersectingCutouts.Add(fi);
                        }

                        cutoutsIntersectingTotal += intersectingCutouts.Count;

                        _logger.Info($"{TAG} wallId={wallId} openingId={opId} intersectingCutouts={intersectingCutouts.Count}");

                        if (intersectingCutouts.Count == 0)
                        {
                            _logger.Info($"{TAG} wallId={wallId} openingId={opId} -> no intersecting cutouts, skip");
                            continue;
                        }

                        // Углы окна (локально)
                        var cornerTL = new XYZ(opBox.Min.X, 0, opBox.Max.Z);
                        var cornerTR = new XYZ(opBox.Max.X, 0, opBox.Max.Z);
                        var cornerBL = new XYZ(opBox.Min.X, 0, opBox.Min.Z);
                        var cornerBR = new XYZ(opBox.Max.X, 0, opBox.Min.Z);

                        // Квадранты относительно центра окна
                        var leftTop = new List<FamilyInstance>();
                        var rightTop = new List<FamilyInstance>();
                        var leftBottom = new List<FamilyInstance>();
                        var rightBottom = new List<FamilyInstance>();

                        foreach (var fi in intersectingCutouts)
                        {
                            var bb = GetLocalBBoxFresh(fi, cw.InverseTransform);
                            if (bb == null) continue;

                            var pc = CenterOf(bb);
                            bool isLeft = pc.X < opCenter.X;
                            bool isTop = pc.Z > opCenter.Z;

                            if (isLeft && isTop) leftTop.Add(fi);
                            else if (!isLeft && isTop) rightTop.Add(fi);
                            else if (isLeft && !isTop) leftBottom.Add(fi);
                            else rightBottom.Add(fi);
                        }

                        // По одному на угол (ближайший к конкретному углу окна)
                        FamilyInstance tl = leftTop.Count > 0 ? PickClosestByXZ(leftTop, cornerTL, cw.InverseTransform) : null;
                        FamilyInstance tr = rightTop.Count > 0 ? PickClosestByXZ(rightTop, cornerTR, cw.InverseTransform) : null;
                        FamilyInstance bl = leftBottom.Count > 0 ? PickClosestByXZ(leftBottom, cornerBL, cw.InverseTransform) : null;
                        FamilyInstance br = rightBottom.Count > 0 ? PickClosestByXZ(rightBottom, cornerBR, cw.InverseTransform) : null;

                        int cornersDetected =
                            (tl != null ? 1 : 0) + (tr != null ? 1 : 0) + (bl != null ? 1 : 0) + (br != null ? 1 : 0);
                        cornersDetectedTotal += cornersDetected;

                        _logger.Info($"{TAG} wallId={wallId} openingId={opId} cornersDetected={cornersDetected} " +
                                        $"TL={(tl?.Id.IntegerValue.ToString() ?? "null")} " +
                                        $"TR={(tr?.Id.IntegerValue.ToString() ?? "null")} " +
                                        $"BL={(bl?.Id.IntegerValue.ToString() ?? "null")} " +
                                        $"BR={(br?.Id.IntegerValue.ToString() ?? "null")}");

                        // Базовые значения пересечения (W=X, H=Z)
                        double tlW = 0, tlH = 0;
                        double trW = 0, trH = 0;
                        double blW = 0, blH = 0;
                        double brW = 0, brH = 0;

                        bool TLok = false, TRok = false, BLok = false, BRok = false;

                        if (tl != null)
                        {
                            var bb = GetLocalBBoxFresh(tl, cw.InverseTransform);
                            TLok = (bb != null) && TryGetBBoxIntersectionSizeXZ(opBox, bb, out tlW, out tlH);
                            _logger.Info($"{TAG} wallId={wallId} openingId={opId} TL panelId={tl.Id.IntegerValue} fam='{tl.Symbol?.Family?.Name}' " +
                                            $"intersectOk={TLok} baseW={tlW * FEET_TO_MM:F1}mm baseH={tlH * FEET_TO_MM:F1}mm");
                        }
                        if (tr != null)
                        {
                            var bb = GetLocalBBoxFresh(tr, cw.InverseTransform);
                            TRok = (bb != null) && TryGetBBoxIntersectionSizeXZ(opBox, bb, out trW, out trH);
                            _logger.Info($"{TAG} wallId={wallId} openingId={opId} TR panelId={tr.Id.IntegerValue} fam='{tr.Symbol?.Family?.Name}' " +
                                            $"intersectOk={TRok} baseW={trW * FEET_TO_MM:F1}mm baseH={trH * FEET_TO_MM:F1}mm");
                        }
                        if (bl != null)
                        {
                            var bb = GetLocalBBoxFresh(bl, cw.InverseTransform);
                            BLok = (bb != null) && TryGetBBoxIntersectionSizeXZ(opBox, bb, out blW, out blH);
                            _logger.Info($"{TAG} wallId={wallId} openingId={opId} BL panelId={bl.Id.IntegerValue} fam='{bl.Symbol?.Family?.Name}' " +
                                            $"intersectOk={BLok} baseW={blW * FEET_TO_MM:F1}mm baseH={blH * FEET_TO_MM:F1}mm");
                        }
                        if (br != null)
                        {
                            var bb = GetLocalBBoxFresh(br, cw.InverseTransform);
                            BRok = (bb != null) && TryGetBBoxIntersectionSizeXZ(opBox, bb, out brW, out brH);
                            _logger.Info($"{TAG} wallId={wallId} openingId={opId} BR panelId={br.Id.IntegerValue} fam='{br.Symbol?.Family?.Name}' " +
                                            $"intersectOk={BRok} baseW={brW * FEET_TO_MM:F1}mm baseH={brH * FEET_TO_MM:F1}mm");
                        }

                        // Общая ширина стороны окна (база)
                        double leftWidth = CombineSideWidth(tlW, blW);
                        double rightWidth = CombineSideWidth(trW, brW);

                        _logger.Info($"{TAG} wallId={wallId} openingId={opId} sideBaseWidths: " +
                                        $"leftWidth={leftWidth * FEET_TO_MM:F1}mm rightWidth={rightWidth * FEET_TO_MM:F1}mm");

                        // Запись с учётом констант по семействам
                        void SetCutout(FamilyInstance fi, string cornerName, double baseWidthFt, double baseHeightFt)
                        {
                            if (fi == null) return;

                            string famName = fi.Symbol?.Family?.Name ?? "";

                            // --- ПОКАЗЫВАЕМ В ЛОГЕ: ЧТО МЫ СОБИРАЕМСЯ ЗАПИСЫВАТЬ ---
                            _logger.Info($"{TAG} wallId={wallId} openingId={opId} {cornerName} panelId={fi.Id.IntegerValue} fam='{famName}' " +
                                            $"baseW={baseWidthFt * FEET_TO_MM:F1}mm baseH={baseHeightFt * FEET_TO_MM:F1}mm");

                            double adjustedW = baseWidthFt;
                            double adjustedH = baseHeightFt;

                            // Применяем правила из вашего ТЗ:
                            // G-family:
                            //   Вырез_Высота: отнять VERTICAL_MM
                            //   Вырез_Ширина: отнять DELTA_MM
                            // L-family:
                            //   Вырез_Высота: отнять HORIZONTAL_MM
                            //   Вырез_Ширина: отнять DELTA_MM
                            if (famName == CUTOUT_G_FAMILY)
                            {
                                adjustedH = baseHeightFt - MmToFt(G_VERTICAL_MM) + MmToFt(WINDOW_CUTOUT_SCALE);
                                adjustedW = baseWidthFt - MmToFt(G_HORIZONTAL_MM) + MmToFt(WINDOW_CUTOUT_SCALE);

                                _logger.Info($"{TAG} {cornerName} panelId={fi.Id.IntegerValue} APPLY G: " +
                                                $"H = baseH - {G_VERTICAL_MM}mm, W = baseW - ({G_HORIZONTAL_MM}mm)");
                            }
                            else if (famName == CUTOUT_L_FAMILY)
                            {
                                adjustedH = baseHeightFt - MmToFt(L_VERTICAL_MM) + MmToFt(WINDOW_CUTOUT_SCALE);
                                adjustedW = baseWidthFt - MmToFt(L_HORIZONTAL_MM) + MmToFt(WINDOW_CUTOUT_SCALE);

                                _logger.Info($"{TAG} {cornerName} panelId={fi.Id.IntegerValue} APPLY L: " +
                                                $"H = baseH - {L_VERTICAL_MM}mm, W = baseW + ({L_HORIZONTAL_MM}mm)");
                            }
                            else
                            {
                                // На всякий: если сюда попало что-то другое — пишем без поправок
                                _logger.Info($"{TAG} {cornerName} panelId={fi.Id.IntegerValue} unknown family -> no offsets");
                            }

                            _logger.Info($"{TAG} wallId={wallId} openingId={opId} {cornerName} panelId={fi.Id.IntegerValue} " +
                                            $"finalW={adjustedW * FEET_TO_MM:F1}mm finalH={adjustedH * FEET_TO_MM:F1}mm");

                            // Защита от отрицательных/нулевых (как у вас: if <= EPS continue)
                            if (baseWidthFt <= EPS || baseHeightFt <= EPS)
                            {
                                _logger.Info($"{TAG} {cornerName} panelId={fi.Id.IntegerValue} baseW/baseH <= 0 -> skip write");
                                return;
                            }
                            if (adjustedW <= EPS || adjustedH <= EPS)
                            {
                                _logger.Info($"{TAG} {cornerName} panelId={fi.Id.IntegerValue} finalW/finalH <= 0 -> skip write");
                                return;
                            }

                            bool setW = TrySetParam(fi, CUT_PARAM_W, adjustedW);
                            bool setH = TrySetParam(fi, CUT_PARAM_H, adjustedH);

                            _logger.Info($"{TAG} {cornerName} panelId={fi.Id.IntegerValue} WRITE " +
                                            $"{CUT_PARAM_W} ok={setW}, {CUT_PARAM_H} ok={setH}");

                            if (setW) paramsSet++;
                            if (setH) paramsSet++;
                            if (setW || setH) cutoutPanelsUpdated++;
                        }

                        // По стороне: ширина общая (left/right), высота индивидуальная по углу
                        if (TLok) SetCutout(tl, "TL", leftWidth, tlH);
                        else if (tl != null) _logger.Info($"{TAG} wallId={wallId} openingId={opId} TL exists but intersection invalid -> skip");

                        if (BLok) SetCutout(bl, "BL", leftWidth, blH);
                        else if (bl != null) _logger.Info($"{TAG} wallId={wallId} openingId={opId} BL exists but intersection invalid -> skip");

                        if (TRok) SetCutout(tr, "TR", rightWidth, trH);
                        else if (tr != null) _logger.Info($"{TAG} wallId={wallId} openingId={opId} TR exists but intersection invalid -> skip");

                        if (BRok) SetCutout(br, "BR", rightWidth, brH);
                        else if (br != null) _logger.Info($"{TAG} wallId={wallId} openingId={opId} BR exists but intersection invalid -> skip");
                    }
                }

                //_doc.Regenerate();
                t.Commit();
            }

            _logger.Info($"{TAG} END: wallsProcessed={wallsProcessed}, openingsProcessed={openingsProcessed}, " +
                            $"cutoutsIntersectingTotal={cutoutsIntersectingTotal}, cornersDetectedTotal={cornersDetectedTotal}, " +
                            $"cutoutPanelsUpdated={cutoutPanelsUpdated}, paramsSet={paramsSet}");
        }

        private void ReplaceRegularPanelsWithCutoutPanels(List<CurtainWallDataDto> data)
        {
            const string REGULAR_FAMILY = REGULAR_PANEL_FAMILY_NAME;

            const string CUTOUT_TOP_FAMILY = G_PANEL_FAMILY_NAME;
            const string CUTOUT_BOTTOM_FAMILY = L_PANEL_FAMILY_NAME;

            // ====== ДОБАВЛЕНО: ИМЕНА ТИПОВ (FamilySymbol) ======
            const string CUTOUT_TOP_FAMILY_TYPE = G_PANEL_FAMILY_NAME_TYPE;
            const string CUTOUT_BOTTOM_TYPE = L_PANEL_FAMILY_NAME_TYPE;

            const double CHECK_SEGMENT_LENGTH_FT = 0.328084;
            const double PANEL_BBOX_REDUCTION_FACTOR = 0.70;

            _logger.Info("[ReplaceRegularPanelsWithCutoutPanels] START");

            if (data == null || data.Count == 0)
            {
                _logger.Info("[ReplaceRegularPanelsWithCutoutPanels] data is null/empty -> skip");
                return;
            }

            // ====== ДОБАВЛЕНО: поиск символа по имени семейства + имени типа ======
            FamilySymbol GetFamilySymbolByFamilyAndType(string familyName, string typeName)
            {
                if (string.IsNullOrWhiteSpace(familyName) || string.IsNullOrWhiteSpace(typeName))
                    return null;

                // Ищем все FamilySymbol в документе
                var symbols = new FilteredElementCollector(_doc)
                    .OfClass(typeof(FamilySymbol))
                    .Cast<FamilySymbol>();

                // Нормализация пробелов + сравнение без учета регистра
                string fn = familyName.Trim();
                string tn = typeName.Trim();

                var symbol = symbols.FirstOrDefault(s =>
                {
                    var fam = s?.Family?.Name?.Trim();
                    var typ = s?.Name?.Trim();
                    return !string.IsNullOrEmpty(fam) && !string.IsNullOrEmpty(typ)
                           && fam.Equals(fn, StringComparison.OrdinalIgnoreCase)
                           && typ.Equals(tn, StringComparison.OrdinalIgnoreCase);
                });

                return symbol;
            }

            XYZ GetCenter(BoundingBoxXYZ b) =>
                new XYZ((b.Min.X + b.Max.X) * 0.5, (b.Min.Y + b.Max.Y) * 0.5, (b.Min.Z + b.Max.Z) * 0.5);

            BoundingBoxXYZ Reduce(BoundingBoxXYZ b, double factor)
            {
                var c = GetCenter(b);
                double hx = (b.Max.X - b.Min.X) * 0.5 * factor;
                double hy = (b.Max.Y - b.Min.Y) * 0.5 * factor;
                double hz = (b.Max.Z - b.Min.Z) * 0.5 * factor;

                return new BoundingBoxXYZ
                {
                    Min = new XYZ(c.X - hx, c.Y - hy, c.Z - hz),
                    Max = new XYZ(c.X + hx, c.Y + hy, c.Z + hz)
                };
            }

            bool BBoxIntersect(BoundingBoxXYZ a, BoundingBoxXYZ b)
            {
                if (a == null || b == null) return false;
                return a.Min.X <= b.Max.X && a.Max.X >= b.Min.X
                    && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y
                    && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;
            }

            bool PointInRect2D(XYZ p, double minX, double maxX, double minZ, double maxZ) =>
                p.X >= minX && p.X <= maxX && p.Z >= minZ && p.Z <= maxZ;

            double Cross2D(XYZ a, XYZ b, XYZ c)
            {
                double abx = b.X - a.X;
                double abz = b.Z - a.Z;
                double acx = c.X - a.X;
                double acz = c.Z - a.Z;
                return abx * acz - abz * acx;
            }

            bool SegmentsIntersect2D(XYZ a, XYZ b, XYZ c, XYZ d)
            {
                const double E = 1e-9;

                double d1 = Cross2D(a, b, c);
                double d2 = Cross2D(a, b, d);
                double d3 = Cross2D(c, d, a);
                double d4 = Cross2D(c, d, b);

                bool Proper = ((d1 > E && d2 < -E) || (d1 < -E && d2 > E)) &&
                              ((d3 > E && d4 < -E) || (d3 < -E && d4 > E));

                if (Proper) return true;

                bool OnSeg(XYZ p, XYZ q, XYZ r)
                {
                    return q.X >= Math.Min(p.X, r.X) - E && q.X <= Math.Max(p.X, r.X) + E &&
                           q.Z >= Math.Min(p.Z, r.Z) - E && q.Z <= Math.Max(p.Z, r.Z) + E;
                }

                bool Collinear(double val) => Math.Abs(val) <= E;

                if (Collinear(d1) && OnSeg(a, c, b)) return true;
                if (Collinear(d2) && OnSeg(a, d, b)) return true;
                if (Collinear(d3) && OnSeg(c, a, d)) return true;
                if (Collinear(d4) && OnSeg(c, b, d)) return true;

                return false;
            }

            bool SegmentIntersectsRect2D(XYZ p1, XYZ p2, BoundingBoxXYZ panelBox)
            {
                if (panelBox == null) return false;

                double minX = Math.Min(panelBox.Min.X, panelBox.Max.X);
                double maxX = Math.Max(panelBox.Min.X, panelBox.Max.X);
                double minZ = Math.Min(panelBox.Min.Z, panelBox.Max.Z);
                double maxZ = Math.Max(panelBox.Min.Z, panelBox.Max.Z);

                if (PointInRect2D(p1, minX, maxX, minZ, maxZ)) return true;
                if (PointInRect2D(p2, minX, maxX, minZ, maxZ)) return true;

                var r1 = new XYZ(minX, 0, minZ);
                var r2 = new XYZ(maxX, 0, minZ);
                var r3 = new XYZ(maxX, 0, maxZ);
                var r4 = new XYZ(minX, 0, maxZ);

                if (SegmentsIntersect2D(p1, p2, r1, r2)) return true;
                if (SegmentsIntersect2D(p1, p2, r2, r3)) return true;
                if (SegmentsIntersect2D(p1, p2, r3, r4)) return true;
                if (SegmentsIntersect2D(p1, p2, r4, r1)) return true;

                return false;
            }

            List<FamilyInstance> GetHitPanelsBySegment2D(List<(FamilyInstance fi, BoundingBoxXYZ bbox)> panels, XYZ s1, XYZ s2)
            {
                var res = new List<FamilyInstance>();
                foreach (var p in panels)
                {
                    if (SegmentIntersectsRect2D(s1, s2, p.bbox))
                        res.Add(p.fi);
                }
                return res;
            }

            // ====== ИЗМЕНЕНО: теперь берём СИМВОЛЫ по семейству+типу ======
            var topSymbol = GetFamilySymbolByFamilyAndType(CUTOUT_TOP_FAMILY, CUTOUT_TOP_FAMILY_TYPE);
            var bottomSymbol = GetFamilySymbolByFamilyAndType(CUTOUT_BOTTOM_FAMILY, CUTOUT_BOTTOM_TYPE);

            if (topSymbol == null || bottomSymbol == null)
            {
                _logger.Info($"[ReplaceRegularPanelsWithCutoutPanels] ERROR: target symbols not found.");
                _logger.Info($"[ReplaceRegularPanelsWithCutoutPanels] Top: Family='{CUTOUT_TOP_FAMILY}', Type='{CUTOUT_TOP_FAMILY_TYPE}', null={topSymbol == null}");
                _logger.Info($"[ReplaceRegularPanelsWithCutoutPanels] Bottom: Family='{CUTOUT_BOTTOM_FAMILY}', Type='{CUTOUT_BOTTOM_TYPE}', null={bottomSymbol == null}");

                TaskDialog.Show("Ошибка",
                    "Не найдены типы (FamilySymbol) для замены угловых панелей.\n" +
                    "Проверь, что в проект загружены нужные семейства и нужные ИМЕНА ТИПОВ совпадают с константами.");
                return;
            }

            int openingsProcessed = 0;
            int replaced = 0;

            var alreadyReplaced = new HashSet<ElementId>();

            using (var t = new Transaction(_doc, "Замена рядовых панелей на угловые"))
            {
                t.Start();

                if (!topSymbol.IsActive) topSymbol.Activate();
                if (!bottomSymbol.IsActive) bottomSymbol.Activate();

                foreach (var wallData in data)
                {
                    if (wallData?.CurtainWallElement == null)
                        continue;

                    var openings = wallData.IntersectingOpenings ?? new List<OpeningModelDto>();
                    var panels = wallData.Panels ?? new List<CurtainWallPanelDto>();

                    var regularPanels = panels
                        .Where(p => p?.PanelElement != null)
                        .Where(p => p.PanelElement.Symbol?.Family?.Name?.Contains(REGULAR_FAMILY) == true)
                        .ToList();

                    if (openings.Count == 0 || regularPanels.Count == 0)
                        continue;

                    foreach (var opening in openings)
                    {
                        if (opening?.OpeningElement == null)
                            continue;

                        var ob = opening.LocalBoundingBox;
                        if (ob == null)
                            continue;

                        openingsProcessed++;

                        var candidate = new List<(FamilyInstance fi, BoundingBoxXYZ bbox)>();
                        foreach (var p in regularPanels)
                        {
                            var pb = p.LocalBoundingBox;
                            if (pb == null) continue;

                            var reduced = Reduce(pb, PANEL_BBOX_REDUCTION_FACTOR);
                            if (BBoxIntersect(ob, reduced))
                                candidate.Add((p.PanelElement, reduced));
                        }

                        if (candidate.Count == 0)
                            continue;

                        var windowCornerTL = new XYZ(ob.Min.X, 0, ob.Max.Z);
                        var windowCornerTR = new XYZ(ob.Max.X, 0, ob.Max.Z);
                        var windowCornerBL = new XYZ(ob.Min.X, 0, ob.Min.Z);
                        var windowCornerBR = new XYZ(ob.Max.X, 0, ob.Min.Z);

                        var corners = new List<(XYZ corner, XYZ dirV, XYZ dirH)>
                {
                    (windowCornerTL, new XYZ(0,0, 1), new XYZ(-1,0,0)),
                    (windowCornerTR, new XYZ(0,0, 1), new XYZ( 1,0,0)),
                    (windowCornerBL, new XYZ(0,0,-1), new XYZ(-1,0,0)),
                    (windowCornerBR, new XYZ(0,0,-1), new XYZ( 1,0,0)),
                };

                        var panelsToReplace = new HashSet<FamilyInstance>();

                        foreach (var c in corners)
                        {
                            var p1v = c.corner;
                            var p2v = c.corner + c.dirV * CHECK_SEGMENT_LENGTH_FT;

                            var p1h = c.corner;
                            var p2h = c.corner + c.dirH * CHECK_SEGMENT_LENGTH_FT;

                            var hitV = GetHitPanelsBySegment2D(candidate, p1v, p2v);
                            var hitH = GetHitPanelsBySegment2D(candidate, p1h, p2h);

                            var common = hitV.Intersect(hitH).ToList();
                            foreach (var fi in common)
                                panelsToReplace.Add(fi);
                        }

                        if (panelsToReplace.Count == 0)
                            continue;

                        var windowCenter = GetCenter(ob);

                        foreach (var panelFi in panelsToReplace)
                        {
                            if (panelFi == null) continue;
                            if (alreadyReplaced.Contains(panelFi.Id)) continue;

                            var pbDto = regularPanels.FirstOrDefault(x => x.PanelElement?.Id == panelFi.Id)?.LocalBoundingBox;
                            if (pbDto == null) continue;

                            var panelCenter = GetCenter(pbDto);
                            bool isTop = panelCenter.Z > windowCenter.Z;

                            var target = isTop ? topSymbol : bottomSymbol;

                            try
                            {
                                if (panelFi.Symbol != null && panelFi.Symbol.Id == target.Id)
                                {
                                    alreadyReplaced.Add(panelFi.Id);
                                    continue;
                                }

                                // ВОТ ТУТ именно и назначается ТИП (FamilySymbol)
                                panelFi.Symbol = target;

                                alreadyReplaced.Add(panelFi.Id);
                                replaced++;
                            }
                            catch (Exception ex)
                            {
                                _logger.Info($"[ReplaceRegularPanelsWithCutoutPanels] panelId={panelFi.Id.IntegerValue} replace ERROR: {ex.Message}");
                            }
                        }
                    }
                }

                t.Commit();
            }

            _logger.Info($"[ReplaceRegularPanelsWithCutoutPanels] END openingsProcessed={openingsProcessed}, replaced={replaced}");
        }

        private void ResetRegularPanelsCutsForIntersectingOpenings(List<CurtainWallDataDto> data)
        {
            const string TAG = "[ResetRegularPanelsCutsForIntersectingOpenings]";
            const string REGULAR_PANEL_FAMILY = REGULAR_PANEL_FAMILY_NAME;
            _logger.Info($"{TAG} START");

            if (data == null || data.Count == 0)
            {
                _logger.Info($"{TAG} data is null/empty -> END");
                return;
            }

            int wallsProcessed = 0;
            int openingsProcessed = 0;
            int panelsTouched = 0;
            int paramsSet = 0;

            try
            {
                using (var t = new Transaction(_doc, "Сброс подрезок рядовых панелей по пересечению с проёмами"))
                {
                    t.Start();

                    foreach (var wallDto in data)
                    {
                        if (wallDto == null || wallDto.CurtainWallElement == null)
                            continue;

                        wallsProcessed++;

                        var openings = wallDto.IntersectingOpenings ?? new List<OpeningModelDto>();
                        var panels = wallDto.Panels ?? new List<CurtainWallPanelDto>();

                        if (openings.Count == 0 || panels.Count == 0)
                            continue;

                        foreach (var opening in openings)
                        {
                            if (opening == null || opening.OpeningElement == null)
                                continue;

                            openingsProcessed++;

                            var opLocal = opening.LocalBoundingBox;
                            if (opLocal == null)
                                continue;

                            var intersectingPanels = new List<CurtainWallPanelDto>();

                            foreach (var p in panels)
                            {
                                if (p == null || p.PanelElement == null)
                                    continue;

                                var fam = p.PanelElement.Symbol?.Family?.Name ?? "";
                                if (!fam.Contains(REGULAR_PANEL_FAMILY))
                                    continue;

                                var pLocal = p.LocalBoundingBox;
                                if (pLocal == null)
                                    continue;

                                if (Intersects3D(opLocal, pLocal))
                                    intersectingPanels.Add(p);
                            }

                            foreach (var p in intersectingPanels)
                            {
                                var fi = p.PanelElement;

                                bool set1 = TrySetDouble(fi, "Подрезка", 0.0);
                                bool set2 = TrySetDouble(fi, "Подрезка_Верх", 0.0);
                                bool set3 = TrySetDouble(fi, "Подрезка_Низ", 0.0);

                                panelsTouched++;
                                if (set1) paramsSet++;
                                if (set2) paramsSet++;
                                if (set3) paramsSet++;
                            }
                        }
                    }

                    t.Commit();
                }

                _logger.Info($"{TAG} END: wallsProcessed={wallsProcessed}, openingsProcessed={openingsProcessed}, panelsTouched={panelsTouched}, paramsSet={paramsSet}");
            }
            catch (Exception ex)
            {
                _logger.Info($"{TAG} ERROR: {ex}");
                TaskDialog.Show("ResetRegularPanelsCutsForIntersectingOpenings", ex.Message);
            }

            bool TrySetDouble(FamilyInstance fi, string paramName, double value)
            {
                try
                {
                    var p = fi.LookupParameter(paramName);
                    if (p == null) return false;
                    if (p.IsReadOnly) return false;
                    p.Set(value);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private void CalculateAndSetRegularPanelsCuts(List<CurtainWallDataDto> data)
        {
            _logger.Info("[CalculateAndSetRegularPanelsCuts] START");
            if (data == null || data.Count == 0)
            {
                _logger.Info("[CalculateAndSetRegularPanelsCuts] data is null/empty -> END");
                return;
            }

            const double DELTA_MM_Regular = -43.0;
            const double VERTICAL_MM_Regular = 7.0;
            const double HORIZONTAL_MM_Regular = 55.0;

            double MmToFt(double mm) => mm / FEET_TO_MM;

            int totalPanelsTouched = 0;
            int totalParamsSet = 0;
            int totalOpeningsProcessed = 0;

            using (Transaction t = new Transaction(_doc, "CW: Set regular panel cuts by openings (local bbox)"))
            {
                t.Start();
                //_doc.Regenerate();

                foreach (var cw in data)
                {
                    if (cw == null || cw.CurtainWallElement == null)
                        continue;

                    var wallId = cw.CurtainWallElement.Id.IntegerValue;
                    var openings = cw.IntersectingOpenings ?? new List<OpeningModelDto>();
                    var panelsAll = cw.Panels ?? new List<CurtainWallPanelDto>();

                    var regularPanels = panelsAll
                        .Where(p => p?.PanelElement != null && p.PanelElement.Symbol?.Family != null)
                        .Where(p => p.PanelElement.Symbol.Family.Name == REGULAR_PANEL_FAMILY_NAME)
                        .ToList();

                    if (openings.Count == 0 || regularPanels.Count == 0)
                        continue;

                    foreach (var op in openings)
                    {
                        if (op?.OpeningElement == null)
                            continue;

                        var opBox = GetLocalBBoxFresh(op.OpeningElement, cw.InverseTransform);
                        if (opBox == null)
                            continue;

                        totalOpeningsProcessed++;
                        var opId = op.OpeningElement.Id.IntegerValue;
                        var opC = CenterOf(opBox);

                        var candidatePanels = new List<(CurtainWallPanelDto dto, BoundingBoxXYZ freshBox)>();
                        foreach (var p in regularPanels)
                        {
                            var fresh = GetLocalBBoxFresh(p.PanelElement, cw.InverseTransform);
                            if (fresh == null) continue;

                            if (Intersects3D(opBox, fresh))
                                candidatePanels.Add((p, fresh));
                        }

                        if (candidatePanels.Count == 0)
                            continue;

                        int panelsTouchedThisOpening = 0;
                        int paramsSetThisOpening = 0;

                        foreach (var item in candidatePanels)
                        {
                            var panel = item.dto.PanelElement;
                            var pId = panel.Id.IntegerValue;

                            var pBox = item.freshBox;
                            if (pBox == null) continue;

                            var pC = CenterOf(pBox);
                            double dx = pC.X - opC.X;
                            double dz = pC.Z - opC.Z;

                            string paramName;
                            double baseValueFt;
                            double adjustedValueFt;

                            if (Math.Abs(dz) >= Math.Abs(dx))
                            {
                                if (dz > 0)
                                {
                                    paramName = "Подрезка_Низ";
                                    baseValueFt = OverlapZ(opBox, pBox);
                                    adjustedValueFt = baseValueFt + MmToFt(VERTICAL_MM_Regular + DELTA_MM_Regular) + MmToFt(WINDOW_CUTOUT_SCALE);
                                }
                                else
                                {
                                    paramName = "Подрезка_Верх";
                                    baseValueFt = OverlapZ(opBox, pBox);
                                    adjustedValueFt = baseValueFt - MmToFt(VERTICAL_MM_Regular) + MmToFt(DELTA_MM_Regular) + MmToFt(WINDOW_CUTOUT_SCALE);
                                }
                            }
                            else
                            {
                                paramName = "Подрезка";
                                baseValueFt = OverlapX(opBox, pBox);
                                adjustedValueFt = baseValueFt - MmToFt(HORIZONTAL_MM_Regular) + MmToFt(DELTA_MM_Regular) + MmToFt(WINDOW_CUTOUT_SCALE);
                            }

                            if (baseValueFt <= EPS) continue;
                            if (adjustedValueFt <= EPS) continue;

                            bool setOk = TrySetParam(panel, paramName, adjustedValueFt);
                            if (setOk)
                            {
                                panelsTouchedThisOpening++;
                                paramsSetThisOpening++;
                            }
                        }

                        totalPanelsTouched += panelsTouchedThisOpening;
                        totalParamsSet += paramsSetThisOpening;
                    }
                }

                //_doc.Regenerate();
                t.Commit();
            }

            _logger.Info($"[CalculateAndSetRegularPanelsCuts] END: openingsProcessed={totalOpeningsProcessed}, panelsTouched={totalPanelsTouched}, paramsSet={totalParamsSet}");
        }


        private List<CurtainWallDataDto> GetElements(Document doc)
        {
            _logger.Info("[CWPanelsCustomizer] GetElements START");

            List<Wall> allCurtainWalls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Where(w => w != null && w.CurtainGrid != null)
                .ToList();

            // Фильтр по выделению
            if (_selMode == PluginSelectionMode.ByWalls && _selectedWallIds != null && _selectedWallIds.Count > 0)
            {
                allCurtainWalls = allCurtainWalls.Where(w => _selectedWallIds.Contains(w.Id)).ToList();
                _logger.Info($"[CWPanelsCustomizer] ByWalls filter → {allCurtainWalls.Count} walls");
            }
            else if (_selMode == PluginSelectionMode.ByPanels && _selectedPanelIds != null)
            {
                // Находим родительские витражи для выделенных панелей
                var parentIds = new HashSet<ElementId>();
                foreach (Wall w in allCurtainWalls)
                {
                    if (w.CurtainGrid == null) continue;
                    foreach (ElementId pid in w.CurtainGrid.GetPanelIds())
                    {
                        if (_selectedPanelIds.Contains(pid)) { parentIds.Add(w.Id); break; }
                    }
                }
                allCurtainWalls = allCurtainWalls.Where(w => parentIds.Contains(w.Id)).ToList();
                _logger.Info($"[CWPanelsCustomizer] ByPanels filter → {allCurtainWalls.Count} parent walls");
            }

            _logger.Info($"[CWPanelsCustomizer] allCurtainWalls={allCurtainWalls.Count}");

            List<FamilyInstance> allOpenings = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_GenericModel)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(fi =>
                    fi.Symbol != null &&
                    fi.Symbol.Family != null &&
                    fi.Symbol.Family.Name != null &&
                    fi.Symbol.Family.Name.Contains("#_Оконный проем_Прямоугольный"))
                .ToList();

            _logger.Info($"[CWPanelsCustomizer] allOpenings={allOpenings.Count}");

            List<CurtainWallDataDto> curtainWallsData = new List<CurtainWallDataDto>();
            Dictionary<ElementId, BoundingBoxXYZ> wallBboxesWorld = new Dictionary<ElementId, BoundingBoxXYZ>();

            foreach (Wall wall in allCurtainWalls)
            {
                Transform wallTransform = GetWallTransform(wall);
                curtainWallsData.Add(new CurtainWallDataDto
                {
                    Id = wall.Id,
                    CurtainWallElement = wall,
                    InverseTransform = wallTransform.Inverse
                });
                wallBboxesWorld[wall.Id] = wall.get_BoundingBox(null);
            }

            foreach (FamilyInstance opening in allOpenings)
            {
                BoundingBoxXYZ openingBboxWorld = opening.get_BoundingBox(null);
                if (openingBboxWorld == null) continue;

                CurtainWallDataDto host = null;
                foreach (CurtainWallDataDto cw in curtainWallsData)
                {
                    if (wallBboxesWorld.TryGetValue(cw.Id, out BoundingBoxXYZ wb) && wb != null && BoundingBoxesIntersect(wb, openingBboxWorld))
                    { host = cw; break; }
                }
                if (host == null) continue;

                host.IntersectingOpenings.Add(new OpeningModelDto
                {
                    Id = opening.Id,
                    OpeningElement = opening,
                    WorldBoundingBox = openingBboxWorld,
                    LocalBoundingBox = TransformBoundingBoxToLocal(openingBboxWorld, host.InverseTransform)
                });
            }

            // Собираем панели только для стен с проёмами (остальные не нужны)
            List<CurtainWallDataDto> wallsInWork = curtainWallsData.Where(x => x.IntersectingOpenings.Any()).ToList();

            foreach (CurtainWallDataDto cw in wallsInWork)
            {
                CurtainGrid grid = cw.CurtainWallElement.CurtainGrid;
                if (grid == null) continue;

                foreach (ElementId pid in grid.GetPanelIds())
                {
                    FamilyInstance panelFi = doc.GetElement(pid) as FamilyInstance;
                    if (panelFi == null) continue;

                    BoundingBoxXYZ panelWorld = panelFi.get_BoundingBox(null);
                    if (panelWorld == null) continue;

                    cw.Panels.Add(new CurtainWallPanelDto
                    {
                        Id = panelFi.Id,
                        PanelElement = panelFi,
                        WorldBoundingBox = panelWorld,
                        LocalBoundingBox = TransformBoundingBoxToLocal(panelWorld, cw.InverseTransform),
                        IsMirrored = false
                    });
                }
            }

            _logger.Info($"[CWPanelsCustomizer] GetElements: walls={curtainWallsData.Count} openings={allOpenings.Count} wallsInWork={wallsInWork.Count} panels={wallsInWork.Sum(w => w.Panels.Count)}");
            return wallsInWork;
        }

        private int GetTotalOpeningsCount(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_GenericModel)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Count(fi =>
                    fi.Symbol != null &&
                    fi.Symbol.Family != null &&
                    fi.Symbol.Family.Name != null &&
                    fi.Symbol.Family.Name.Contains("#_Оконный проем_Прямоугольный"));
        }

        private int GetTotalCurtainWallsCount(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Count(w => w != null && w.CurtainGrid != null);
        }

        private Transform GetWallTransform(Wall curWall)
        {
            Transform result = Transform.Identity;
            if (curWall == null) return result;

            LocationCurve lc = curWall.Location as LocationCurve;
            if (lc == null)
                return result;

            Line line = lc.Curve as Line;
            if (line == null)
                return result;

            bool isFlipped = curWall.Flipped;
            XYZ orientation = curWall.Orientation;

            XYZ ptStart = line.GetEndPoint(0);
            XYZ ptEnd = line.GetEndPoint(1);

            Transform transf = Transform.Identity;
            transf.BasisZ = XYZ.BasisZ;

            XYZ vectorX = ptEnd - ptStart;

            bool isLinkedHasReflection = false;
            bool isWallFlippedInLinkFile = isLinkedHasReflection ? !isFlipped : isFlipped;

            if (isWallFlippedInLinkFile == false)
            {
                transf.BasisX = vectorX.Negate().Normalize();
                transf.BasisY = orientation;
                transf.Origin = ptEnd;
            }
            else
            {
                transf.BasisX = vectorX.Normalize();
                transf.BasisY = orientation.Negate();
                transf.Origin = ptStart;
            }

            return transf;
        }

        private bool BoundingBoxesIntersect(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            if (a == null || b == null) return false;

            bool no =
                a.Max.X < b.Min.X || a.Min.X > b.Max.X ||
                a.Max.Y < b.Min.Y || a.Min.Y > b.Max.Y ||
                a.Max.Z < b.Min.Z || a.Min.Z > b.Max.Z;

            return !no;
        }

        private BoundingBoxXYZ TransformBoundingBoxToLocal(BoundingBoxXYZ worldBbox, Transform inverseTransform)
        {
            if (worldBbox == null || inverseTransform == null) return null;

            double[] xs = { worldBbox.Min.X, worldBbox.Max.X };
            double[] ys = { worldBbox.Min.Y, worldBbox.Max.Y };
            double[] zs = { worldBbox.Min.Z, worldBbox.Max.Z };

            List<XYZ> pts = new List<XYZ>(8);
            foreach (double x in xs)
                foreach (double y in ys)
                    foreach (double z in zs)
                        pts.Add(inverseTransform.OfPoint(new XYZ(x, y, z)));

            double minX = pts.Min(p => p.X);
            double minY = pts.Min(p => p.Y);
            double minZ = pts.Min(p => p.Z);
            double maxX = pts.Max(p => p.X);
            double maxY = pts.Max(p => p.Y);
            double maxZ = pts.Max(p => p.Z);

            return new BoundingBoxXYZ
            {
                Min = new XYZ(minX, minY, minZ),
                Max = new XYZ(maxX, maxY, maxZ)
            };
        }

        private FamilySymbol GetFamilySymbolByName(string familyName)
        {
            try
            {
                var family = new FilteredElementCollector(_doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault(f => f.Name == familyName);

                if (family == null)
                    return null;

                var symbolIds = family.GetFamilySymbolIds();
                if (symbolIds == null || symbolIds.Count == 0)
                    return null;

                var firstSymbolId = symbolIds.First();
                return _doc.GetElement(firstSymbolId) as FamilySymbol;
            }
            catch
            {
                return null;
            }
        }

        // ===========================
        // ===== SHARED HELPERS ======
        // ===========================
        private XYZ CenterOf(BoundingBoxXYZ b) =>
            new XYZ((b.Min.X + b.Max.X) * 0.5, (b.Min.Y + b.Max.Y) * 0.5, (b.Min.Z + b.Max.Z) * 0.5);

        private bool Intersects3D(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            if (a == null || b == null) return false;
            return !(a.Max.X < b.Min.X || a.Min.X > b.Max.X ||
                     a.Max.Y < b.Min.Y || a.Min.Y > b.Max.Y ||
                     a.Max.Z < b.Min.Z || a.Min.Z > b.Max.Z);
        }

        private double OverlapX(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            double min = Math.Max(a.Min.X, b.Min.X);
            double max = Math.Min(a.Max.X, b.Max.X);
            double o = max - min;
            return o > EPS ? o : 0.0;
        }

        private double OverlapZ(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            double min = Math.Max(a.Min.Z, b.Min.Z);
            double max = Math.Min(a.Max.Z, b.Max.Z);
            double o = max - min;
            return o > EPS ? o : 0.0;
        }

        private bool TrySetParam(FamilyInstance fi, string name, double valFt)
        {
            if (fi == null) return false;
            Parameter p = fi.LookupParameter(name);
            if (p == null) return false;
            if (p.IsReadOnly) return false;
            try
            {
                p.Set(valFt);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private bool TryGetDoubleParam(FamilyInstance fi, string name, out double valueFt)
        {
            valueFt = 0.0;
            if (fi == null) return false;

            try
            {
                var p = fi.LookupParameter(name);
                if (p == null) return false;
                valueFt = p.AsDouble();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private BoundingBoxXYZ GetLocalBBoxFresh(Element e, Transform inverseTransform)
        {
            if (e == null || inverseTransform == null) return null;
            var wb = e.get_BoundingBox(null);
            if (wb == null) return null;
            return TransformBoundingBoxToLocal(wb, inverseTransform);
        }

        private BoundingBoxXYZ GetWorldBBoxFresh(Element e)
        {
            if (e == null) return null;
            return e.get_BoundingBox(null);
        }

        // === CONVERT PANELS INSIDE OPENINGS TO EMPTY PANELS ===

        private void ConvertPanelsInsideOpeningsToEmpty(List<CurtainWallDataDto> data)
        {
            const string TAG = "[ConvertPanelsInsideOpeningsToEmpty]";

            ElementId emptyTypeId = FindEmptyPanelTypeId(_doc);
            if (emptyTypeId == null)
            {
                _logger.Warn($"{TAG} Empty panel type not found.");
                LogAvailableCurtainPanelTypes();
                return;
            }

            bool CenterInside(BoundingBoxXYZ p, BoundingBoxXYZ o)
            {
                double cx = (p.Min.X + p.Max.X) * 0.5, cz = (p.Min.Z + p.Max.Z) * 0.5;
                return cx >= o.Min.X && cx <= o.Max.X && cz >= o.Min.Z && cz <= o.Max.Z;
            }

            int converted = 0, skipped = 0, errors = 0;
            using (Transaction tx = new Transaction(_doc, "Convert panels inside openings to empty"))
            {
                tx.Start();
                foreach (CurtainWallDataDto cw in data)
                foreach (OpeningModelDto opening in cw.IntersectingOpenings)
                {
                    if (opening.LocalBoundingBox == null) continue;
                    foreach (CurtainWallPanelDto panel in cw.Panels)
                    {
                        if (panel.LocalBoundingBox == null || !CenterInside(panel.LocalBoundingBox, opening.LocalBoundingBox)) continue;
                        Element elem = _doc.GetElement(panel.Id);
                        if (elem == null || !elem.IsValidObject || elem.GetTypeId() == emptyTypeId) { skipped++; continue; }
                        try
                        {
                            bool wasPinned = elem.Pinned;
                            if (wasPinned) elem.Pinned = false;
                            elem.ChangeTypeId(emptyTypeId);
                            if (wasPinned) { Element a = _doc.GetElement(panel.Id); if (a?.IsValidObject == true) a.Pinned = true; }
                            converted++;
                            _logger.Info($"{TAG} CONVERTED panelId={panel.Id.IntegerValue} openingId={opening.Id.IntegerValue} wallId={cw.Id.IntegerValue}");
                        }
                        catch (Exception ex) { errors++; _logger.Warn($"{TAG} FAILED panelId={panel.Id.IntegerValue}: {ex.Message}"); }
                    }
                }
                tx.Commit();
            }
            _logger.LogSummary(TAG, ("converted", converted), ("skipped", skipped), ("errors", errors));
        }

        private ElementId FindEmptyPanelTypeId(Document doc)
        {
            var sym = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(fs => fs.Category?.Id?.IntegerValue == (int)BuiltInCategory.OST_CurtainWallPanels
                                   && fs.FamilyName.IndexOf("Пустая", StringComparison.OrdinalIgnoreCase) >= 0);
            if (sym != null)
            {
                _logger.Info($"[FindEmptyPanelTypeId] FamilySymbol: Family='{sym.FamilyName}' Type='{sym.Name}' Id={sym.Id.IntegerValue}");
                return sym.Id;
            }

            var wt = new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<WallType>()
                .FirstOrDefault(w => w.Kind == WallKind.Curtain
                                  && new[] { "Пустая", "Пусто", "Empty" }.Any(s => w.Name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0));
            if (wt != null)
            {
                _logger.Info($"[FindEmptyPanelTypeId] WallType: Name='{wt.Name}' Id={wt.Id.IntegerValue}");
                return wt.Id;
            }

            return null;
        }

        private void LogAvailableCurtainPanelTypes()
        {
            _logger.Info("[LogAvailableCurtainPanelTypes] FamilySymbols:");
            new FilteredElementCollector(_doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .Where(fs => fs.Category?.Id?.IntegerValue == (int)BuiltInCategory.OST_CurtainWallPanels)
                .ToList().ForEach(s => _logger.Info($"  Id={s.Id.IntegerValue} Family='{s.FamilyName}' Type='{s.Name}'"));

            _logger.Info("[LogAvailableCurtainPanelTypes] WallTypes (Curtain):");
            new FilteredElementCollector(_doc).OfClass(typeof(WallType)).Cast<WallType>()
                .Where(wt => wt.Kind == WallKind.Curtain)
                .ToList().ForEach(wt => _logger.Info($"  Id={wt.Id.IntegerValue} Name='{wt.Name}'"));
        }
    }
}
