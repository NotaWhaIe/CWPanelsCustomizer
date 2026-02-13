using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace CWPanelsCustomizer
{
    public class CurtainWallPanelDto
    {
        public ElementId Id { get; set; }
        public FamilyInstance PanelElement { get; set; }
        public BoundingBoxXYZ WorldBoundingBox { get; set; }
        public BoundingBoxXYZ LocalBoundingBox { get; set; }

        // DTO-источник истины для зеркальности панели (по ТЗ)
        public bool IsMirrored { get; set; }

        // ✅ НОВОЕ: ориентация панели относительно окна (в локале витража)
        public PanelSideRelativeToOpening SideRelativeToOpening { get; set; }
            = PanelSideRelativeToOpening.Undefined;

        // (опционально, но ОЧЕНЬ полезно для отладки)
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

        private const double EPS = 1e-9;
        private const double FEET_TO_MM = 304.8;


        // ГЛОБАЛЬНОЕ УПРАВЛЕНИЕ РАЗМЕРОМ ОКОННОГО ВЫРЕЗА (мм)
        // сделать, чтоб тут нужно было ввести 10
        private const double WINDOW_CUTOUT_SCALE = 0.0;


        // Имена панелей
        //private const string REGULAR_PANEL_FAMILY_NAME = "КРСТ_НВФ_Уголвая_В2.1";
        private const string REGULAR_PANEL_FAMILY_NAME = "КРСТ_НВФ_Рядовая_В3";//старый вариант
        private const string REGULAR_PANEL_FAMILY_NAME_TYPE = "RAL 5005";

        private const string G_PANEL_FAMILY_NAME = "КРСТ_НВФ_С Г-образным вырезом_В2";
        private const string G_PANEL_FAMILY_NAME_TYPE = "RAL 5005";

        private const string L_PANEL_FAMILY_NAME = "КРСТ_НВФ_С L-образным вырезом";
        private const string L_PANEL_FAMILY_NAME_TYPE = "RAL 5005";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            _uidoc = commandData.Application.ActiveUIDocument;
            _doc = _uidoc.Document;

            Debug.WriteLine("[CWPanelsCustomizer] Execute START");

            using (TransactionGroup tg = new TransactionGroup(_doc, IS_NAME))
            {
                tg.Start();

                // 1) Замена АР кассет на КР
                ReplaceArCurtainPanelsWithKrPanels(_doc);

                // 2) Сбор данных
                List<CurtainWallDataDto> data = GetElements(_doc);

                // 3) Сброс подрезок рядовых панелей по пересечению с проёмами
                ResetRegularPanelsCutsForIntersectingOpenings(data);

                // 4) Замена рядовых панелей на угловые (где нужно)
                ReplaceRegularPanelsWithCutoutPanels(data);

                // 5) НОВОЕ: отзеркаливание панелей справа от окна, пересекающихся с BB окна
                MirrorPanelsRightOfOpenings(data);

                // 6) Подрезки рядовых панелей
                CalculateAndSetRegularPanelsCuts(data);

                // 7) Настройка угловых панелей по значениям рядовых
                CalculateAndSetCutoutPanelsCuts(data);


                int totalOpenings = GetTotalOpeningsCount(_doc);
                int totalCurtainWalls = GetTotalCurtainWallsCount(_doc);
                int wallsInWork = data.Count;
                int totalAssignedOpenings = data.Sum(x => x.IntersectingOpenings.Count);

                Debug.WriteLine("[CWPanelsCustomizer] Summary:");
                Debug.WriteLine($"[CWPanelsCustomizer] Total openings: {totalOpenings}");
                Debug.WriteLine($"[CWPanelsCustomizer] Total curtain walls: {totalCurtainWalls}");
                Debug.WriteLine($"[CWPanelsCustomizer] Walls in work: {wallsInWork}");
                Debug.WriteLine($"[CWPanelsCustomizer] Total assigned openings: {totalAssignedOpenings}");

                tg.Assimilate();
            }

            Debug.WriteLine("[CWPanelsCustomizer] Execute END");
            return Result.Succeeded;
        }

        private void ReplaceArCurtainPanelsWithKrPanels(Document doc)
        {
            const string TAG = "[ReplaceArCurtainPanelsWithKrPanels]";

            // Целевой КР-тип
            const string TARGET_KR_PANEL_FAMILY_NAME = REGULAR_PANEL_FAMILY_NAME;
            const string TARGET_KR_PANEL_TYPE_NAME = REGULAR_PANEL_FAMILY_NAME_TYPE;

            // Критерий "АР-панели" (по твоему RevitLookup: WallType = "AP_Кассета")
            // Если у тебя есть другой префикс/правило — меняй только эту константу/проверку ниже.
            const string AR_TYPE_PREFIX_1 = "AP_";
            const string AR_TYPE_PREFIX_2 = "АР";

            if (doc == null)
            {
                throw new ArgumentNullException(nameof(doc));
            }

            // 1) Собираем ВСЕ панели витража (как Wall), но работаем только с Id
            List<ElementId> allPanelIds = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_CurtainWallPanels)
                .WhereElementIsNotElementType()
                .OfType<Wall>()
                .Select(w => w.Id)
                .ToList();

            Debug.WriteLine($"{TAG} Found panels (total): {allPanelIds.Count}");

            if (allPanelIds.Count == 0)
            {
                Debug.WriteLine($"{TAG} No curtain panel walls found. Skip.");
                return;
            }

            // 2) Отбираем ТОЛЬКО АР панели (вне транзакции), остальные не трогаем
            List<ElementId> arPanelIds = new List<ElementId>(allPanelIds.Count);
            int skippedNotAr = 0;
            int skippedInvalidAtScan = 0;

            foreach (ElementId id in allPanelIds)
            {
                Element e = doc.GetElement(id);
                if (e == null || !e.IsValidObject)
                {
                    skippedInvalidAtScan++;
                    continue;
                }

                Wall w = e as Wall;
                if (w == null)
                {
                    skippedInvalidAtScan++;
                    continue;
                }

                ElementType t = doc.GetElement(w.GetTypeId()) as ElementType;
                if (t == null)
                {
                    skippedInvalidAtScan++;
                    continue;
                }

                string typeName = t.Name ?? string.Empty;

                bool isAr =
                    typeName.StartsWith(AR_TYPE_PREFIX_1, StringComparison.OrdinalIgnoreCase) ||
                    typeName.StartsWith(AR_TYPE_PREFIX_2, StringComparison.OrdinalIgnoreCase);

                if (isAr)
                {
                    arPanelIds.Add(id);
                }
                else
                {
                    skippedNotAr++;
                }
            }

            Debug.WriteLine($"{TAG} AR panels found: {arPanelIds.Count}");
            Debug.WriteLine($"{TAG} Skipped (not AR): {skippedNotAr}");
            Debug.WriteLine($"{TAG} Skipped (invalid at scan): {skippedInvalidAtScan}");

            // Если АР панелей нет — просто выходим (как ты и просил)
            if (arPanelIds.Count == 0)
            {
                Debug.WriteLine($"{TAG} No AR panels to replace. Skip method.");
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

            Debug.WriteLine($"{TAG} Target symbol: Family='{targetSymbol.Family.Name}', Type='{targetSymbol.Name}', Id={targetSymbol.Id.IntegerValue}");

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
                            Debug.WriteLine($"{TAG} SKIP (invalid object). PanelId={panelIdInt}");
                            continue;
                        }

                        Wall panel = element as Wall;
                        if (panel == null)
                        {
                            skippedInvalid++;
                            Debug.WriteLine($"{TAG} SKIP (not a Wall anymore). PanelId={panelIdInt}");
                            continue;
                        }

                        // Уже нужный КР-тип — не трогаем
                        if (panel.GetTypeId() == targetTypeId)
                        {
                            skippedAlreadyKrType++;
                            continue;
                        }

                        bool wasPinned = panel.Pinned;
                        if (wasPinned)
                        {
                            panel.Pinned = false;
                        }

                        // ВАЖНО: после ChangeTypeId объект может стать невалидным
                        panel.ChangeTypeId(targetTypeId);

                        // Возвращаем pinned через повторное получение элемента по Id
                        if (wasPinned)
                        {
                            Element after = doc.GetElement(panelId);
                            if (after != null && after.IsValidObject)
                            {
                                after.Pinned = true;
                            }
                        }

                        replaced++;
                        Debug.WriteLine($"{TAG} REPLACED (AR->KR). PanelId={panelIdInt} -> Family='{TARGET_KR_PANEL_FAMILY_NAME}', Type='{TARGET_KR_PANEL_TYPE_NAME}'");
                    }
                    catch (Autodesk.Revit.Exceptions.InvalidObjectException)
                    {
                        skippedInvalid++;
                        Debug.WriteLine($"{TAG} SKIP (InvalidObjectException). PanelId={panelIdInt}");
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        Debug.WriteLine($"{TAG} FAILED. PanelId={panelIdInt}. Error: {ex.Message}");
                    }
                }

                tx.Commit();
            }

            Debug.WriteLine($"{TAG} SUMMARY:");
            Debug.WriteLine($"{TAG}  AR panels processed: {arPanelIds.Count}");
            Debug.WriteLine($"{TAG}  Replaced (AR->KR): {replaced}");
            Debug.WriteLine($"{TAG}  Skipped (already KR type): {skippedAlreadyKrType}");
            Debug.WriteLine($"{TAG}  Skipped (invalid): {skippedInvalid}");
            Debug.WriteLine($"{TAG}  Failed: {failed}");
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

            Debug.WriteLine($"{TAG} START");

            if (data == null || data.Count == 0)
            {
                Debug.WriteLine($"{TAG} data is null/empty -> END");
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

                    Debug.WriteLine($"{TAG} wallId={wallId} openings={openings.Count} panels={panels.Count}");

                    if (openings.Count == 0 || panels.Count == 0)
                        continue;

                    foreach (var opening in openings)
                    {
                        if (opening?.OpeningElement == null)
                            continue;

                        var obLocalFresh = GetLocalBBoxFresh(opening.OpeningElement, cw.InverseTransform);
                        if (obLocalFresh == null)
                        {
                            Debug.WriteLine($"{TAG} wallId={wallId} openingId={opening.Id.IntegerValue} obLocal=null -> skip");
                            continue;
                        }

                        openingsProcessed++;
                        int opId = opening.OpeningElement.Id.IntegerValue;

                        var obLocal = ExpandXZ(obLocalFresh, bandExpandFt);
                        var wCenterX = CenterOf(obLocalFresh).X;

                        Debug.WriteLine($"{TAG} wallId={wallId} openingId={opId} windowCenterX(local)={wCenterX:F4}");

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
                                    Debug.WriteLine($"{TAG} panelId={fi.Id.IntegerValue} cannot flip -> skip");
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
                                Debug.WriteLine($"{TAG} ERROR flip wallId={wallId} openingId={opId} panelId={fi.Id.IntegerValue}: {ex}");
                                SetCommentSafe(fi, debug + " | RESULT=ERROR");
                                // processedPanels.Add(fi.Id) уже стоит — чтобы не зациклиться на падающей панели
                            }
                        }
                    }
                }

                //_doc.Regenerate();
                t.Commit();
            }

            Debug.WriteLine($"{TAG} END wallsProcessed={wallsProcessed} openingsProcessed={openingsProcessed}");
            Debug.WriteLine($"{TAG} panelsSeen={panelsSeen}");
            Debug.WriteLine($"{TAG} bbIntersectTypeMatched={bbIntersectTypeMatched}");
            Debug.WriteLine($"{TAG} needMirrorCandidates={needMirrorCandidates}");
            Debug.WriteLine($"{TAG} flippedOk={flippedOk}");
            Debug.WriteLine($"{TAG} skippedAlreadyProcessed={skippedAlreadyProcessed}");
            Debug.WriteLine($"{TAG} skippedNoFlip={skippedNoFlip}");
            Debug.WriteLine($"{TAG} flipErrors={flipErrors}");
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

            Debug.WriteLine($"{TAG} START");

            if (data == null || data.Count == 0)
            {
                Debug.WriteLine($"{TAG} data is null/empty -> END");
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

                    Debug.WriteLine($"{TAG} wallId={wallId} openings={openings.Count} panels={panelsAll.Count}");

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

                    Debug.WriteLine($"{TAG} wallId={wallId} cutoutPanels={cutoutPanels.Count}");
                    if (cutoutPanels.Count == 0)
                        continue;

                    foreach (var op in openings)
                    {
                        if (op?.OpeningElement == null)
                            continue;

                        var opBox = GetLocalBBoxFresh(op.OpeningElement, cw.InverseTransform);
                        if (opBox == null)
                        {
                            Debug.WriteLine($"{TAG} wallId={wallId} openingId={op.Id.IntegerValue} opBox=null -> skip");
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

                        Debug.WriteLine($"{TAG} wallId={wallId} openingId={opId} intersectingCutouts={intersectingCutouts.Count}");

                        if (intersectingCutouts.Count == 0)
                        {
                            Debug.WriteLine($"{TAG} wallId={wallId} openingId={opId} -> no intersecting cutouts, skip");
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

                        Debug.WriteLine($"{TAG} wallId={wallId} openingId={opId} cornersDetected={cornersDetected} " +
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
                            Debug.WriteLine($"{TAG} wallId={wallId} openingId={opId} TL panelId={tl.Id.IntegerValue} fam='{tl.Symbol?.Family?.Name}' " +
                                            $"intersectOk={TLok} baseW={tlW * FEET_TO_MM:F1}mm baseH={tlH * FEET_TO_MM:F1}mm");
                        }
                        if (tr != null)
                        {
                            var bb = GetLocalBBoxFresh(tr, cw.InverseTransform);
                            TRok = (bb != null) && TryGetBBoxIntersectionSizeXZ(opBox, bb, out trW, out trH);
                            Debug.WriteLine($"{TAG} wallId={wallId} openingId={opId} TR panelId={tr.Id.IntegerValue} fam='{tr.Symbol?.Family?.Name}' " +
                                            $"intersectOk={TRok} baseW={trW * FEET_TO_MM:F1}mm baseH={trH * FEET_TO_MM:F1}mm");
                        }
                        if (bl != null)
                        {
                            var bb = GetLocalBBoxFresh(bl, cw.InverseTransform);
                            BLok = (bb != null) && TryGetBBoxIntersectionSizeXZ(opBox, bb, out blW, out blH);
                            Debug.WriteLine($"{TAG} wallId={wallId} openingId={opId} BL panelId={bl.Id.IntegerValue} fam='{bl.Symbol?.Family?.Name}' " +
                                            $"intersectOk={BLok} baseW={blW * FEET_TO_MM:F1}mm baseH={blH * FEET_TO_MM:F1}mm");
                        }
                        if (br != null)
                        {
                            var bb = GetLocalBBoxFresh(br, cw.InverseTransform);
                            BRok = (bb != null) && TryGetBBoxIntersectionSizeXZ(opBox, bb, out brW, out brH);
                            Debug.WriteLine($"{TAG} wallId={wallId} openingId={opId} BR panelId={br.Id.IntegerValue} fam='{br.Symbol?.Family?.Name}' " +
                                            $"intersectOk={BRok} baseW={brW * FEET_TO_MM:F1}mm baseH={brH * FEET_TO_MM:F1}mm");
                        }

                        // Общая ширина стороны окна (база)
                        double leftWidth = CombineSideWidth(tlW, blW);
                        double rightWidth = CombineSideWidth(trW, brW);

                        Debug.WriteLine($"{TAG} wallId={wallId} openingId={opId} sideBaseWidths: " +
                                        $"leftWidth={leftWidth * FEET_TO_MM:F1}mm rightWidth={rightWidth * FEET_TO_MM:F1}mm");

                        // Запись с учётом констант по семействам
                        void SetCutout(FamilyInstance fi, string cornerName, double baseWidthFt, double baseHeightFt)
                        {
                            if (fi == null) return;

                            string famName = fi.Symbol?.Family?.Name ?? "";

                            // --- ПОКАЗЫВАЕМ В ЛОГЕ: ЧТО МЫ СОБИРАЕМСЯ ЗАПИСЫВАТЬ ---
                            Debug.WriteLine($"{TAG} wallId={wallId} openingId={opId} {cornerName} panelId={fi.Id.IntegerValue} fam='{famName}' " +
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

                                Debug.WriteLine($"{TAG} {cornerName} panelId={fi.Id.IntegerValue} APPLY G: " +
                                                $"H = baseH - {G_VERTICAL_MM}mm, W = baseW - ({G_HORIZONTAL_MM}mm)");
                            }
                            else if (famName == CUTOUT_L_FAMILY)
                            {
                                adjustedH = baseHeightFt - MmToFt(L_VERTICAL_MM) + MmToFt(WINDOW_CUTOUT_SCALE);
                                adjustedW = baseWidthFt - MmToFt(L_HORIZONTAL_MM) + MmToFt(WINDOW_CUTOUT_SCALE);

                                Debug.WriteLine($"{TAG} {cornerName} panelId={fi.Id.IntegerValue} APPLY L: " +
                                                $"H = baseH - {L_VERTICAL_MM}mm, W = baseW + ({L_HORIZONTAL_MM}mm)");
                            }
                            else
                            {
                                // На всякий: если сюда попало что-то другое — пишем без поправок
                                Debug.WriteLine($"{TAG} {cornerName} panelId={fi.Id.IntegerValue} unknown family -> no offsets");
                            }

                            Debug.WriteLine($"{TAG} wallId={wallId} openingId={opId} {cornerName} panelId={fi.Id.IntegerValue} " +
                                            $"finalW={adjustedW * FEET_TO_MM:F1}mm finalH={adjustedH * FEET_TO_MM:F1}mm");

                            // Защита от отрицательных/нулевых (как у вас: if <= EPS continue)
                            if (baseWidthFt <= EPS || baseHeightFt <= EPS)
                            {
                                Debug.WriteLine($"{TAG} {cornerName} panelId={fi.Id.IntegerValue} baseW/baseH <= 0 -> skip write");
                                return;
                            }
                            if (adjustedW <= EPS || adjustedH <= EPS)
                            {
                                Debug.WriteLine($"{TAG} {cornerName} panelId={fi.Id.IntegerValue} finalW/finalH <= 0 -> skip write");
                                return;
                            }

                            bool setW = TrySetParam(fi, CUT_PARAM_W, adjustedW);
                            bool setH = TrySetParam(fi, CUT_PARAM_H, adjustedH);

                            Debug.WriteLine($"{TAG} {cornerName} panelId={fi.Id.IntegerValue} WRITE " +
                                            $"{CUT_PARAM_W} ok={setW}, {CUT_PARAM_H} ok={setH}");

                            if (setW) paramsSet++;
                            if (setH) paramsSet++;
                            if (setW || setH) cutoutPanelsUpdated++;
                        }

                        // По стороне: ширина общая (left/right), высота индивидуальная по углу
                        if (TLok) SetCutout(tl, "TL", leftWidth, tlH);
                        else if (tl != null) Debug.WriteLine($"{TAG} wallId={wallId} openingId={opId} TL exists but intersection invalid -> skip");

                        if (BLok) SetCutout(bl, "BL", leftWidth, blH);
                        else if (bl != null) Debug.WriteLine($"{TAG} wallId={wallId} openingId={opId} BL exists but intersection invalid -> skip");

                        if (TRok) SetCutout(tr, "TR", rightWidth, trH);
                        else if (tr != null) Debug.WriteLine($"{TAG} wallId={wallId} openingId={opId} TR exists but intersection invalid -> skip");

                        if (BRok) SetCutout(br, "BR", rightWidth, brH);
                        else if (br != null) Debug.WriteLine($"{TAG} wallId={wallId} openingId={opId} BR exists but intersection invalid -> skip");
                    }
                }

                //_doc.Regenerate();
                t.Commit();
            }

            Debug.WriteLine($"{TAG} END: wallsProcessed={wallsProcessed}, openingsProcessed={openingsProcessed}, " +
                            $"cutoutsIntersectingTotal={cutoutsIntersectingTotal}, cornersDetectedTotal={cornersDetectedTotal}, " +
                            $"cutoutPanelsUpdated={cutoutPanelsUpdated}, paramsSet={paramsSet}");
        }


        //private void ReplaceRegularPanelsWithCutoutPanels(List<CurtainWallDataDto> data)
        //{
        //    const string REGULAR_FAMILY = REGULAR_PANEL_FAMILY_NAME;

        //    const string CUTOUT_TOP_FAMILY = G_PANEL_FAMILY_NAME;
        //    const string CUTOUT_TOP_FAMILY_TYPE = G_PANEL_FAMILY_NAME_TYPE;


        //    const string CUTOUT_BOTTOM_FAMILY = L_PANEL_FAMILY_NAME;
        //    const string CUTOUT_BOTTOM_TYPE = L_PANEL_FAMILY_NAME_TYPE;





        //    const double CHECK_SEGMENT_LENGTH_FT = 0.328084;
        //    const double PANEL_BBOX_REDUCTION_FACTOR = 0.70;

        //    Debug.WriteLine("[ReplaceRegularPanelsWithCutoutPanels] START");

        //    if (data == null || data.Count == 0)
        //    {
        //        Debug.WriteLine("[ReplaceRegularPanelsWithCutoutPanels] data is null/empty -> skip");
        //        return;
        //    }

        //    XYZ GetCenter(BoundingBoxXYZ b) =>
        //        new XYZ((b.Min.X + b.Max.X) * 0.5, (b.Min.Y + b.Max.Y) * 0.5, (b.Min.Z + b.Max.Z) * 0.5);

        //    BoundingBoxXYZ Reduce(BoundingBoxXYZ b, double factor)
        //    {
        //        var c = GetCenter(b);
        //        double hx = (b.Max.X - b.Min.X) * 0.5 * factor;
        //        double hy = (b.Max.Y - b.Min.Y) * 0.5 * factor;
        //        double hz = (b.Max.Z - b.Min.Z) * 0.5 * factor;

        //        return new BoundingBoxXYZ
        //        {
        //            Min = new XYZ(c.X - hx, c.Y - hy, c.Z - hz),
        //            Max = new XYZ(c.X + hx, c.Y + hy, c.Z + hz)
        //        };
        //    }

        //    bool BBoxIntersect(BoundingBoxXYZ a, BoundingBoxXYZ b)
        //    {
        //        if (a == null || b == null) return false;
        //        return a.Min.X <= b.Max.X && a.Max.X >= b.Min.X
        //            && a.Min.Y <= b.Max.Y && a.Max.Y >= b.Min.Y
        //            && a.Min.Z <= b.Max.Z && a.Max.Z >= b.Min.Z;
        //    }

        //    bool PointInRect2D(XYZ p, double minX, double maxX, double minZ, double maxZ) =>
        //        p.X >= minX && p.X <= maxX && p.Z >= minZ && p.Z <= maxZ;

        //    double Cross2D(XYZ a, XYZ b, XYZ c)
        //    {
        //        double abx = b.X - a.X;
        //        double abz = b.Z - a.Z;
        //        double acx = c.X - a.X;
        //        double acz = c.Z - a.Z;
        //        return abx * acz - abz * acx;
        //    }

        //    bool SegmentsIntersect2D(XYZ a, XYZ b, XYZ c, XYZ d)
        //    {
        //        const double E = 1e-9;

        //        double d1 = Cross2D(a, b, c);
        //        double d2 = Cross2D(a, b, d);
        //        double d3 = Cross2D(c, d, a);
        //        double d4 = Cross2D(c, d, b);

        //        bool Proper = ((d1 > E && d2 < -E) || (d1 < -E && d2 > E)) &&
        //                      ((d3 > E && d4 < -E) || (d3 < -E && d4 > E));

        //        if (Proper) return true;

        //        bool OnSeg(XYZ p, XYZ q, XYZ r)
        //        {
        //            return q.X >= Math.Min(p.X, r.X) - E && q.X <= Math.Max(p.X, r.X) + E &&
        //                   q.Z >= Math.Min(p.Z, r.Z) - E && q.Z <= Math.Max(p.Z, r.Z) + E;
        //        }

        //        bool Collinear(double val) => Math.Abs(val) <= E;

        //        if (Collinear(d1) && OnSeg(a, c, b)) return true;
        //        if (Collinear(d2) && OnSeg(a, d, b)) return true;
        //        if (Collinear(d3) && OnSeg(c, a, d)) return true;
        //        if (Collinear(d4) && OnSeg(c, b, d)) return true;

        //        return false;
        //    }

        //    bool SegmentIntersectsRect2D(XYZ p1, XYZ p2, BoundingBoxXYZ panelBox)
        //    {
        //        if (panelBox == null) return false;

        //        double minX = Math.Min(panelBox.Min.X, panelBox.Max.X);
        //        double maxX = Math.Max(panelBox.Min.X, panelBox.Max.X);
        //        double minZ = Math.Min(panelBox.Min.Z, panelBox.Max.Z);
        //        double maxZ = Math.Max(panelBox.Min.Z, panelBox.Max.Z);

        //        if (PointInRect2D(p1, minX, maxX, minZ, maxZ)) return true;
        //        if (PointInRect2D(p2, minX, maxX, minZ, maxZ)) return true;

        //        var r1 = new XYZ(minX, 0, minZ);
        //        var r2 = new XYZ(maxX, 0, minZ);
        //        var r3 = new XYZ(maxX, 0, maxZ);
        //        var r4 = new XYZ(minX, 0, maxZ);

        //        if (SegmentsIntersect2D(p1, p2, r1, r2)) return true;
        //        if (SegmentsIntersect2D(p1, p2, r2, r3)) return true;
        //        if (SegmentsIntersect2D(p1, p2, r3, r4)) return true;
        //        if (SegmentsIntersect2D(p1, p2, r4, r1)) return true;

        //        return false;
        //    }

        //    List<FamilyInstance> GetHitPanelsBySegment2D(List<(FamilyInstance fi, BoundingBoxXYZ bbox)> panels, XYZ s1, XYZ s2)
        //    {
        //        var res = new List<FamilyInstance>();
        //        foreach (var p in panels)
        //        {
        //            if (SegmentIntersectsRect2D(s1, s2, p.bbox))
        //                res.Add(p.fi);
        //        }
        //        return res;
        //    }

        //    var topSymbol = GetFamilySymbolByName(CUTOUT_TOP_FAMILY);
        //    var bottomSymbol = GetFamilySymbolByName(CUTOUT_BOTTOM_FAMILY);

        //    if (topSymbol == null || bottomSymbol == null)
        //    {
        //        Debug.WriteLine($"[ReplaceRegularPanelsWithCutoutPanels] ERROR: target symbols not found. Top null={topSymbol == null}, Bottom null={bottomSymbol == null}");
        //        TaskDialog.Show("Ошибка", "Не найдены семейства для замены угловых панелей (проверь имена семейств в проекте).");
        //        return;
        //    }

        //    int openingsProcessed = 0;
        //    int replaced = 0;

        //    var alreadyReplaced = new HashSet<ElementId>();

        //    using (var t = new Transaction(_doc, "Замена рядовых панелей на угловые"))
        //    {
        //        t.Start();

        //        if (!topSymbol.IsActive) topSymbol.Activate();
        //        if (!bottomSymbol.IsActive) bottomSymbol.Activate();

        //        foreach (var wallData in data)
        //        {
        //            if (wallData?.CurtainWallElement == null)
        //                continue;

        //            var openings = wallData.IntersectingOpenings ?? new List<OpeningModelDto>();
        //            var panels = wallData.Panels ?? new List<CurtainWallPanelDto>();

        //            var regularPanels = panels
        //                .Where(p => p?.PanelElement != null)
        //                .Where(p => p.PanelElement.Symbol?.Family?.Name?.Contains(REGULAR_FAMILY) == true)
        //                .ToList();

        //            if (openings.Count == 0 || regularPanels.Count == 0)
        //                continue;

        //            foreach (var opening in openings)
        //            {
        //                if (opening?.OpeningElement == null)
        //                    continue;

        //                var ob = opening.LocalBoundingBox;
        //                if (ob == null)
        //                    continue;

        //                openingsProcessed++;

        //                var candidate = new List<(FamilyInstance fi, BoundingBoxXYZ bbox)>();
        //                foreach (var p in regularPanels)
        //                {
        //                    var pb = p.LocalBoundingBox;
        //                    if (pb == null) continue;

        //                    var reduced = Reduce(pb, PANEL_BBOX_REDUCTION_FACTOR);
        //                    if (BBoxIntersect(ob, reduced))
        //                        candidate.Add((p.PanelElement, reduced));
        //                }

        //                if (candidate.Count == 0)
        //                    continue;

        //                var windowCornerTL = new XYZ(ob.Min.X, 0, ob.Max.Z);
        //                var windowCornerTR = new XYZ(ob.Max.X, 0, ob.Max.Z);
        //                var windowCornerBL = new XYZ(ob.Min.X, 0, ob.Min.Z);
        //                var windowCornerBR = new XYZ(ob.Max.X, 0, ob.Min.Z);

        //                var corners = new List<(XYZ corner, XYZ dirV, XYZ dirH)>
        //                {
        //                    (windowCornerTL, new XYZ(0,0, 1), new XYZ(-1,0,0)),
        //                    (windowCornerTR, new XYZ(0,0, 1), new XYZ( 1,0,0)),
        //                    (windowCornerBL, new XYZ(0,0,-1), new XYZ(-1,0,0)),
        //                    (windowCornerBR, new XYZ(0,0,-1), new XYZ( 1,0,0)),
        //                };

        //                var panelsToReplace = new HashSet<FamilyInstance>();

        //                foreach (var c in corners)
        //                {
        //                    var p1v = c.corner;
        //                    var p2v = c.corner + c.dirV * CHECK_SEGMENT_LENGTH_FT;

        //                    var p1h = c.corner;
        //                    var p2h = c.corner + c.dirH * CHECK_SEGMENT_LENGTH_FT;

        //                    var hitV = GetHitPanelsBySegment2D(candidate, p1v, p2v);
        //                    var hitH = GetHitPanelsBySegment2D(candidate, p1h, p2h);

        //                    var common = hitV.Intersect(hitH).ToList();
        //                    foreach (var fi in common)
        //                        panelsToReplace.Add(fi);
        //                }

        //                if (panelsToReplace.Count == 0)
        //                    continue;

        //                var windowCenter = GetCenter(ob);

        //                foreach (var panelFi in panelsToReplace)
        //                {
        //                    if (panelFi == null) continue;
        //                    if (alreadyReplaced.Contains(panelFi.Id)) continue;

        //                    var pbDto = regularPanels.FirstOrDefault(x => x.PanelElement?.Id == panelFi.Id)?.LocalBoundingBox;
        //                    if (pbDto == null) continue;

        //                    var panelCenter = GetCenter(pbDto);
        //                    bool isTop = panelCenter.Z > windowCenter.Z;

        //                    var target = isTop ? topSymbol : bottomSymbol;

        //                    try
        //                    {
        //                        if (panelFi.Symbol != null && panelFi.Symbol.Id == target.Id)
        //                        {
        //                            alreadyReplaced.Add(panelFi.Id);
        //                            continue;
        //                        }

        //                        panelFi.Symbol = target;
        //                        alreadyReplaced.Add(panelFi.Id);
        //                        replaced++;
        //                    }
        //                    catch (Exception ex)
        //                    {
        //                        Debug.WriteLine($"[ReplaceRegularPanelsWithCutoutPanels] panelId={panelFi.Id.IntegerValue} replace ERROR: {ex.Message}");
        //                    }
        //                }
        //            }
        //        }

        //        t.Commit();
        //    }

        //    Debug.WriteLine($"[ReplaceRegularPanelsWithCutoutPanels] END openingsProcessed={openingsProcessed}, replaced={replaced}");
        //}


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

            Debug.WriteLine("[ReplaceRegularPanelsWithCutoutPanels] START");

            if (data == null || data.Count == 0)
            {
                Debug.WriteLine("[ReplaceRegularPanelsWithCutoutPanels] data is null/empty -> skip");
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
                Debug.WriteLine($"[ReplaceRegularPanelsWithCutoutPanels] ERROR: target symbols not found.");
                Debug.WriteLine($"[ReplaceRegularPanelsWithCutoutPanels] Top: Family='{CUTOUT_TOP_FAMILY}', Type='{CUTOUT_TOP_FAMILY_TYPE}', null={topSymbol == null}");
                Debug.WriteLine($"[ReplaceRegularPanelsWithCutoutPanels] Bottom: Family='{CUTOUT_BOTTOM_FAMILY}', Type='{CUTOUT_BOTTOM_TYPE}', null={bottomSymbol == null}");

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
                                Debug.WriteLine($"[ReplaceRegularPanelsWithCutoutPanels] panelId={panelFi.Id.IntegerValue} replace ERROR: {ex.Message}");
                            }
                        }
                    }
                }

                t.Commit();
            }

            Debug.WriteLine($"[ReplaceRegularPanelsWithCutoutPanels] END openingsProcessed={openingsProcessed}, replaced={replaced}");
        }

        private void ResetRegularPanelsCutsForIntersectingOpenings(List<CurtainWallDataDto> data)
        {
            const string TAG = "[ResetRegularPanelsCutsForIntersectingOpenings]";
            const string REGULAR_PANEL_FAMILY = REGULAR_PANEL_FAMILY_NAME;
            Debug.WriteLine($"{TAG} START");

            if (data == null || data.Count == 0)
            {
                Debug.WriteLine($"{TAG} data is null/empty -> END");
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

                Debug.WriteLine($"{TAG} END: wallsProcessed={wallsProcessed}, openingsProcessed={openingsProcessed}, panelsTouched={panelsTouched}, paramsSet={paramsSet}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"{TAG} ERROR: {ex}");
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
            Debug.WriteLine("[CalculateAndSetRegularPanelsCuts] START");
            if (data == null || data.Count == 0)
            {
                Debug.WriteLine("[CalculateAndSetRegularPanelsCuts] data is null/empty -> END");
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

            Debug.WriteLine($"[CalculateAndSetRegularPanelsCuts] END: openingsProcessed={totalOpeningsProcessed}, panelsTouched={totalPanelsTouched}, paramsSet={totalParamsSet}");
        }


        private List<CurtainWallDataDto> GetElements(Document doc)
        {
            Debug.WriteLine("[CWPanelsCustomizer] GetElements START");

            List<Wall> allCurtainWalls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Where(w => w != null && w.CurtainGrid != null)
                .ToList();

            Debug.WriteLine($"[CWPanelsCustomizer] allCurtainWalls={allCurtainWalls.Count}");

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

            Debug.WriteLine($"[CWPanelsCustomizer] allOpenings={allOpenings.Count}");

            List<CurtainWallDataDto> curtainWallsData = new List<CurtainWallDataDto>();
            Dictionary<ElementId, BoundingBoxXYZ> wallBboxesWorld = new Dictionary<ElementId, BoundingBoxXYZ>();

            foreach (Wall wall in allCurtainWalls)
            {
                BoundingBoxXYZ wallBboxWorld = wall.get_BoundingBox(null);

                Transform wallTransform = GetWallTransform(wall);
                Transform inverseTransform = wallTransform.Inverse;

                CurtainWallDataDto cwDto = new CurtainWallDataDto
                {
                    Id = wall.Id,
                    CurtainWallElement = wall,
                    InverseTransform = inverseTransform
                };

                curtainWallsData.Add(cwDto);
                wallBboxesWorld[wall.Id] = wallBboxWorld;

                Debug.WriteLine($"[CWPanelsCustomizer] wall Id={wall.Id.IntegerValue} bboxWorld={(wallBboxWorld == null ? "null" : "ok")}");
            }

            foreach (FamilyInstance opening in allOpenings)
            {
                BoundingBoxXYZ openingBboxWorld = opening.get_BoundingBox(null);
                if (openingBboxWorld == null)
                {
                    Debug.WriteLine($"[CWPanelsCustomizer] opening Id={opening.Id.IntegerValue} bboxWorld=null skip");
                    continue;
                }

                CurtainWallDataDto host = null;

                foreach (CurtainWallDataDto cw in curtainWallsData)
                {
                    if (!wallBboxesWorld.TryGetValue(cw.Id, out BoundingBoxXYZ wallBboxWorld) || wallBboxWorld == null)
                        continue;

                    if (BoundingBoxesIntersect(wallBboxWorld, openingBboxWorld))
                    {
                        host = cw;
                        break;
                    }
                }

                if (host == null)
                {
                    Debug.WriteLine($"[CWPanelsCustomizer] opening Id={opening.Id.IntegerValue} intersects no wall");
                    continue;
                }

                BoundingBoxXYZ openingLocal = TransformBoundingBoxToLocal(openingBboxWorld, host.InverseTransform);

                host.IntersectingOpenings.Add(new OpeningModelDto
                {
                    Id = opening.Id,
                    OpeningElement = opening,
                    WorldBoundingBox = openingBboxWorld,
                    LocalBoundingBox = openingLocal
                });

                Debug.WriteLine($"[CWPanelsCustomizer] opening Id={opening.Id.IntegerValue} assigned to wall Id={host.Id.IntegerValue}");
            }

            foreach (CurtainWallDataDto cw in curtainWallsData)
            {
                CurtainGrid grid = cw.CurtainWallElement.CurtainGrid;
                if (grid == null) continue;

                ICollection<ElementId> panelIds = grid.GetPanelIds();
                Debug.WriteLine($"[CWPanelsCustomizer] wall Id={cw.Id.IntegerValue} panelIds={panelIds.Count}");

                foreach (ElementId pid in panelIds)
                {
                    FamilyInstance panelFi = doc.GetElement(pid) as FamilyInstance;
                    if (panelFi == null)
                        continue;

                    BoundingBoxXYZ panelWorld = panelFi.get_BoundingBox(null);
                    if (panelWorld == null)
                    {
                        Debug.WriteLine($"[CWPanelsCustomizer] panel Id={pid.IntegerValue} bboxWorld=null skip");
                        continue;
                    }

                    BoundingBoxXYZ panelLocal = TransformBoundingBoxToLocal(panelWorld, cw.InverseTransform);

                    cw.Panels.Add(new CurtainWallPanelDto
                    {
                        Id = panelFi.Id,
                        PanelElement = panelFi,
                        WorldBoundingBox = panelWorld,
                        LocalBoundingBox = panelLocal,
                        IsMirrored = false
                    });
                }

                Debug.WriteLine($"[CWPanelsCustomizer] wall Id={cw.Id.IntegerValue} panelsFilled={cw.Panels.Count}");
            }

            List<CurtainWallDataDto> wallsInWork = curtainWallsData.Where(x => x.IntersectingOpenings.Any()).ToList();
            Debug.WriteLine($"[CWPanelsCustomizer] wallsInWork={wallsInWork.Count}");

            Debug.WriteLine("[CWPanelsCustomizer] GetElements END");
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

            Debug.WriteLine($"[CWPanelsCustomizer] GetWallTransform wall Id={curWall.Id.IntegerValue} Origin=({transf.Origin.X:F3},{transf.Origin.Y:F3},{transf.Origin.Z:F3})");
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
    }
}
