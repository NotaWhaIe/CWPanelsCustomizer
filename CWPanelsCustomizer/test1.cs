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

    [Transaction(TransactionMode.Manual)]
    public class test1 : IExternalCommand
    {
        public static string IS_NAME => "!!!!!_Настроить кассеты";
        public static string IS_DESCRIPTION => "*Что делает плагин?";
        public static string IS_TAB_NAME => "#BIM";
        public static string IS_IMAGE => "CWPanelsCustomizer.Images.a1.png";

        private UIDocument _uidoc;
        private Document _doc;

        // --- Common constants ---
        private const string REGULAR_PANEL_FAMILY = "КРСТ_НВФ_Рядовая_В3";
        private const string OPENING_FAMILY_MARKER = "#_Оконный проем_Прямоугольный";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            _uidoc = commandData.Application.ActiveUIDocument;
            _doc = _uidoc.Document;

            Log("[CWPanelsCustomizer] Execute START");

            using (var tg = new TransactionGroup(_doc, IS_NAME))
            {
                tg.Start();

                // 0) Сбор данных
                List<CurtainWallDataDto> data = GetElements(_doc);

                // 1) Сброс подрезок рядовых панелей
                ResetRegularPanelsCutsForIntersectingOpenings(data);

                // 2) Замена рядовых на угловые в местах реальных углов проёмов
                ReplaceRegularPanelsWithCutoutPanels(data);

                // FIX: НЕ ВЫЗЫВАЕМ _doc.Regenerate() тут, т.к. мы вне Transaction -> InvalidOperationException

                // 3) Настройка рядовых кассет (берём свежие bbox внутри Transaction + Regenerate)
                CalculateAndSetRegularPanelsCuts(data);

                // summary
                int totalOpenings = GetTotalOpeningsCount(_doc);
                int totalCurtainWalls = GetTotalCurtainWallsCount(_doc);
                int wallsInWork = data?.Count ?? 0;
                int totalAssignedOpenings = data?.Sum(x => x?.IntersectingOpenings?.Count ?? 0) ?? 0;

                Log("[CWPanelsCustomizer] Summary:");
                Log($"[CWPanelsCustomizer] Total openings: {totalOpenings}");
                Log($"[CWPanelsCustomizer] Total curtain walls: {totalCurtainWalls}");
                Log($"[CWPanelsCustomizer] Walls in work: {wallsInWork}");
                Log($"[CWPanelsCustomizer] Total assigned openings: {totalAssignedOpenings}");

                tg.Assimilate();
            }

            Log("[CWPanelsCustomizer] Execute END");
            return Result.Succeeded;
        }

        private void ReplaceRegularPanelsWithCutoutPanels(List<CurtainWallDataDto> data)
        {
            const string TAG = "[ReplaceRegularPanelsWithCutoutPanels]";
            const string CUTOUT_TOP_FAMILY = "КРСТ_НВФ_С Г-образным вырезом_В2";
            const string CUTOUT_BOTTOM_FAMILY = "КРСТ_НВФ_С L-образным вырезом";

            const double CHECK_SEGMENT_LENGTH_FT = 0.328084;  // 100 мм в футах
            const double PANEL_BBOX_REDUCTION_FACTOR = 0.70;  // как в референсе

            Log($"{TAG} START");

            if (data == null || data.Count == 0)
            {
                Log($"{TAG} data is null/empty -> skip");
                return;
            }

            var topSymbol = GetFamilySymbolByName(CUTOUT_TOP_FAMILY);
            var bottomSymbol = GetFamilySymbolByName(CUTOUT_BOTTOM_FAMILY);

            if (topSymbol == null || bottomSymbol == null)
            {
                Log($"{TAG} ERROR: target symbols not found. Top='{CUTOUT_TOP_FAMILY}' null={topSymbol == null}, Bottom='{CUTOUT_BOTTOM_FAMILY}' null={bottomSymbol == null}");
                TaskDialog.Show("Ошибка", "Не найдены семейства для замены угловых панелей (проверь имена семейств в проекте).");
                return;
            }

            int openingsProcessed = 0;
            int panelsToReplaceTotal = 0;
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

                    var wallId = wallData.Id;
                    var openings = wallData.IntersectingOpenings ?? new List<OpeningModelDto>();
                    var panels = wallData.Panels ?? new List<CurtainWallPanelDto>();

                    var regularPanels = panels
                        .Where(p => p?.PanelElement != null)
                        .Where(p => p.PanelElement.Symbol?.Family?.Name?.Contains(REGULAR_PANEL_FAMILY) == true)
                        .ToList();

                    Log($"{TAG} wallId={wallId.IntegerValue} openings={openings.Count}, regularPanels={regularPanels.Count}");

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

                        Log($"{TAG} wallId={wallId.IntegerValue} openingId={opening.Id.IntegerValue} opLocalMin=({ob.Min.X:F4},{ob.Min.Y:F4},{ob.Min.Z:F4}) opLocalMax=({ob.Max.X:F4},{ob.Max.Y:F4},{ob.Max.Z:F4})");

                        // ШАГ 1: кандидаты по пересечению bbox (в ЛСК) + редукция bbox панели
                        var candidate = new List<(FamilyInstance fi, BoundingBoxXYZ bbox)>();
                        foreach (var p in regularPanels)
                        {
                            var pb = p.LocalBoundingBox;
                            if (pb == null) continue;

                            var reduced = ReduceBoundingBoxAroundCenter(pb, PANEL_BBOX_REDUCTION_FACTOR);
                            if (BoundingBoxesIntersectLocal(ob, reduced))
                                candidate.Add((p.PanelElement, reduced));
                        }

                        Log($"{TAG} wallId={wallId.IntegerValue} openingId={opening.Id.IntegerValue} candidatePanels={candidate.Count}");

                        if (candidate.Count == 0)
                            continue;

                        // ШАГ 2: углы окна в плоскости XZ ЛСК (Y не важен)
                        var windowCornerTL = new XYZ(ob.Min.X, 0, ob.Max.Z);
                        var windowCornerTR = new XYZ(ob.Max.X, 0, ob.Max.Z);
                        var windowCornerBL = new XYZ(ob.Min.X, 0, ob.Min.Z);
                        var windowCornerBR = new XYZ(ob.Max.X, 0, ob.Min.Z);

                        var corners = new (XYZ corner, XYZ dirV, XYZ dirH, string name)[]
                        {
                            (windowCornerTL, new XYZ(0,0, 1), new XYZ(-1,0,0), "TL"),
                            (windowCornerTR, new XYZ(0,0, 1), new XYZ( 1,0,0), "TR"),
                            (windowCornerBL, new XYZ(0,0,-1), new XYZ(-1,0,0), "BL"),
                            (windowCornerBR, new XYZ(0,0,-1), new XYZ( 1,0,0), "BR"),
                        };

                        var panelsToReplace = new HashSet<FamilyInstance>();

                        foreach (var c in corners)
                        {
                            XYZ p1v = c.corner;
                            XYZ p2v = c.corner + c.dirV * CHECK_SEGMENT_LENGTH_FT;

                            XYZ p1h = c.corner;
                            XYZ p2h = c.corner + c.dirH * CHECK_SEGMENT_LENGTH_FT;

                            var hitV = GetHitPanelsBySegment2D(candidate, p1v, p2v);
                            var hitH = GetHitPanelsBySegment2D(candidate, p1h, p2h);

                            var common = hitV.Intersect(hitH).ToList();
                            if (common.Count == 0) continue;

                            foreach (var fi in common)
                                panelsToReplace.Add(fi);

                            Log($"{TAG} wallId={wallId.IntegerValue} openingId={opening.Id.IntegerValue} corner={c.name} commonPanels={common.Count}");
                        }

                        Log($"{TAG} wallId={wallId.IntegerValue} openingId={opening.Id.IntegerValue} cornerPanelsFound={panelsToReplace.Count}");

                        if (panelsToReplace.Count == 0)
                            continue;

                        // ШАГ 3: верх/низ по Z центров (в ЛСК)
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
                            var targetName = isTop ? CUTOUT_TOP_FAMILY : CUTOUT_BOTTOM_FAMILY;

                            try
                            {
                                if (panelFi.Symbol != null && panelFi.Symbol.Id == target.Id)
                                {
                                    alreadyReplaced.Add(panelFi.Id);
                                    continue;
                                }

                                panelFi.Symbol = target;

                                panelsToReplaceTotal++;
                                replaced++;
                                alreadyReplaced.Add(panelFi.Id);

                                Log($"{TAG} wallId={wallId.IntegerValue} openingId={opening.Id.IntegerValue} panelId={panelFi.Id.IntegerValue} replaced -> {targetName}");
                            }
                            catch (Exception ex)
                            {
                                Log($"{TAG} panelId={panelFi.Id.IntegerValue} replace ERROR: {ex.Message}");
                            }
                        }
                    }
                }

                t.Commit();
            }

            Log($"{TAG} END openingsProcessed={openingsProcessed}, panelsToReplaceTotal={panelsToReplaceTotal}, replaced={replaced}");
        }

        private void ResetRegularPanelsCutsForIntersectingOpenings(List<CurtainWallDataDto> data)
        {
            const string TAG = "[ResetRegularPanelsCutsForIntersectingOpenings]";
            Log($"{TAG} START");

            if (data == null || data.Count == 0)
            {
                Log($"{TAG} data is null/empty -> END");
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
                        if (wallDto?.CurtainWallElement == null)
                            continue;

                        wallsProcessed++;

                        var openings = wallDto.IntersectingOpenings ?? new List<OpeningModelDto>();
                        var panels = wallDto.Panels ?? new List<CurtainWallPanelDto>();

                        Log($"{TAG} wallId={wallDto.Id?.IntegerValue}, openings={openings.Count}, panels={panels.Count}");

                        if (openings.Count == 0 || panels.Count == 0)
                            continue;

                        foreach (var opening in openings)
                        {
                            if (opening?.OpeningElement == null)
                                continue;

                            openingsProcessed++;

                            var opLocal = opening.LocalBoundingBox;
                            if (opLocal == null)
                            {
                                Log($"{TAG} wallId={wallDto.Id.IntegerValue}, openingId={opening.Id.IntegerValue} opLocal=null skip");
                                continue;
                            }

                            Log($"{TAG} wallId={wallDto.Id.IntegerValue}, openingId={opening.Id.IntegerValue}, opLocalMin=({opLocal.Min.X:F4},{opLocal.Min.Y:F4},{opLocal.Min.Z:F4}), opLocalMax=({opLocal.Max.X:F4},{opLocal.Max.Y:F4},{opLocal.Max.Z:F4})");

                            var intersectingPanels = panels
                                .Where(p => p?.PanelElement != null)
                                .Where(p => (p.PanelElement.Symbol?.Family?.Name ?? "").Contains(REGULAR_PANEL_FAMILY))
                                .Where(p => p.LocalBoundingBox != null && BoundingBoxesIntersectLocal(opLocal, p.LocalBoundingBox))
                                .ToList();

                            Log($"{TAG} wallId={wallDto.Id.IntegerValue}, openingId={opening.Id.IntegerValue}, intersectingRegularPanels={intersectingPanels.Count}");

                            foreach (var p in intersectingPanels)
                            {
                                var fi = p.PanelElement;

                                bool set1 = TrySetDouble(fi, "Подрезка", 0.0, TAG);
                                bool set2 = TrySetDouble(fi, "Подрезка_Верх", 0.0, TAG);
                                bool set3 = TrySetDouble(fi, "Подрезка_Низ", 0.0, TAG);

                                panelsTouched++;
                                if (set1) paramsSet++;
                                if (set2) paramsSet++;
                                if (set3) paramsSet++;

                                Log($"{TAG} wallId={wallDto.Id.IntegerValue}, openingId={opening.Id.IntegerValue}, panelId={fi.Id.IntegerValue}, reset(Подрезка={set1}, Подрезка_Верх={set2}, Подрезка_Низ={set3})");
                            }
                        }
                    }

                    t.Commit();
                }

                Log($"{TAG} END: wallsProcessed={wallsProcessed}, openingsProcessed={openingsProcessed}, panelsTouched={panelsTouched}, paramsSet={paramsSet}");
            }
            catch (Exception ex)
            {
                Log($"{TAG} ERROR: {ex}");
                TaskDialog.Show(nameof(ResetRegularPanelsCutsForIntersectingOpenings), ex.Message);
            }
        }

        private void CalculateAndSetRegularPanelsCuts(List<CurtainWallDataDto> data)
        {
            const string TAG = "[CalculateAndSetRegularPanelsCuts]";
            Log($"{TAG} START");

            if (data == null || data.Count == 0)
            {
                Log($"{TAG} data is null/empty -> END");
                return;
            }

            const double EPS = 1e-9;
            const double FEET_TO_MM = 304.8;

            // === Adjustments (mm) ===
            const double DELTA_MM = -43.0;
            const double VERTICAL_MM = 7.0;
            const double HORIZONTAL_MM = 55.0;

            double MmToFt(double mm) => mm / FEET_TO_MM;

            int totalPanelsTouched = 0;
            int totalParamsSet = 0;
            int totalOpeningsProcessed = 0;

            using (var t = new Transaction(_doc, "CW: Set regular panel cuts by openings (local bbox)"))
            {
                t.Start();

                // ВАЖНО: regenerate внутри Transaction (валидно и нужно после замены символов)
                _doc.Regenerate();

                foreach (var cw in data)
                {
                    if (cw?.CurtainWallElement == null)
                    {
                        Log($"{TAG} skip: null cw or wall");
                        continue;
                    }

                    int wallId = cw.CurtainWallElement.Id.IntegerValue;
                    var openings = cw.IntersectingOpenings ?? new List<OpeningModelDto>();
                    var panelsAll = cw.Panels ?? new List<CurtainWallPanelDto>();

                    var regularPanels = panelsAll
                        .Where(p => p?.PanelElement != null)
                        .Where(p => p.PanelElement.Symbol?.Family?.Name == REGULAR_PANEL_FAMILY)
                        .ToList();

                    Log($"{TAG} wallId={wallId}, openings={openings.Count}, regularPanels={regularPanels.Count}");

                    if (openings.Count == 0 || regularPanels.Count == 0)
                        continue;

                    foreach (var op in openings)
                    {
                        if (op?.OpeningElement == null)
                        {
                            Log($"{TAG} wallId={wallId}: skip opening null");
                            continue;
                        }

                        var opBox = GetLocalBBoxFresh(op.OpeningElement, cw.InverseTransform);
                        if (opBox == null)
                        {
                            Log($"{TAG} wallId={wallId}: skip opening bbox null");
                            continue;
                        }

                        totalOpeningsProcessed++;
                        int opId = op.OpeningElement.Id.IntegerValue;
                        var opC = GetCenter(opBox);

                        Log($"{TAG} wallId={wallId}, openingId={opId}, opLocalMin=({opBox.Min.X:F4},{opBox.Min.Y:F4},{opBox.Min.Z:F4}), opLocalMax=({opBox.Max.X:F4},{opBox.Max.Y:F4},{opBox.Max.Z:F4})");

                        // ВАЖНО: берём свежие bbox панелей после Replace + Regenerate
                        var candidatePanels = new List<(FamilyInstance panel, BoundingBoxXYZ freshBox)>();
                        foreach (var p in regularPanels)
                        {
                            var fresh = GetLocalBBoxFresh(p.PanelElement, cw.InverseTransform);
                            if (fresh == null) continue;

                            if (BoundingBoxesIntersectLocal(opBox, fresh))
                                candidatePanels.Add((p.PanelElement, fresh));
                        }

                        Log($"{TAG} wallId={wallId}, openingId={opId}, candidatePanels={candidatePanels.Count}");

                        if (candidatePanels.Count == 0)
                            continue;

                        int touchedThisOpening = 0;
                        int setThisOpening = 0;

                        foreach (var item in candidatePanels)
                        {
                            var panel = item.panel;
                            int pId = panel.Id.IntegerValue;
                            var pBox = item.freshBox;

                            var pC = GetCenter(pBox);
                            double dx = pC.X - opC.X;
                            double dz = pC.Z - opC.Z;

                            string side;
                            string paramName;
                            double baseValueFt;
                            double adjustedValueFt;

                            if (Math.Abs(dz) >= Math.Abs(dx))
                            {
                                // vertical (Top/Bottom)
                                baseValueFt = OverlapZ(opBox, pBox, EPS);
                                if (dz > 0)
                                {
                                    side = "Top";
                                    paramName = "Подрезка_Низ";
                                    adjustedValueFt = baseValueFt + MmToFt(VERTICAL_MM + DELTA_MM);
                                }
                                else
                                {
                                    side = "Bottom";
                                    paramName = "Подрезка_Верх";
                                    adjustedValueFt = baseValueFt - MmToFt(VERTICAL_MM) + MmToFt(DELTA_MM);
                                }
                            }
                            else
                            {
                                // horizontal (Left/Right)
                                side = dx < 0 ? "Left" : "Right";
                                paramName = "Подрезка";
                                baseValueFt = OverlapX(opBox, pBox, EPS);
                                adjustedValueFt = baseValueFt - MmToFt(HORIZONTAL_MM) + MmToFt(DELTA_MM);
                            }

                            if (baseValueFt <= EPS)
                            {
                                Log($"{TAG} wallId={wallId}, openingId={opId}, panelId={pId}, side={side}: overlap=0 -> skip");
                                continue;
                            }

                            if (adjustedValueFt <= EPS)
                            {
                                Log($"{TAG} wallId={wallId}, openingId={opId}, panelId={pId}, side={side}: adjusted<=0 (baseFt={baseValueFt:F6}) -> skip");
                                continue;
                            }

                            bool setOk = TrySetParam(panel, paramName, adjustedValueFt);

                            Log($"{TAG} wallId={wallId}, openingId={opId}, panelId={pId}, side={side}, param={paramName}, " +
                                $"baseFt={baseValueFt:F6} ({baseValueFt * FEET_TO_MM:F1}mm), adjFt={adjustedValueFt:F6} ({adjustedValueFt * FEET_TO_MM:F1}mm), set={setOk}");

                            if (setOk)
                            {
                                touchedThisOpening++;
                                setThisOpening++;
                            }
                        }

                        totalPanelsTouched += touchedThisOpening;
                        totalParamsSet += setThisOpening;

                        Log($"{TAG} wallId={wallId}, openingId={opId}: touchedPanels={touchedThisOpening}, paramsSet={setThisOpening}");
                    }
                }

                _doc.Regenerate();
                t.Commit();
            }

            Log($"{TAG} END: openingsProcessed={totalOpeningsProcessed}, panelsTouched={totalPanelsTouched}, paramsSet={totalParamsSet}");
        }

        /// <summary>
        /// Первый метод фасада: собирает витражи, проёмы и панели,
        /// строит inverse transform витража и преобразует BBox в локальную СК витража.
        /// Возвращает ТОЛЬКО витражи "в работе" (у которых есть пересекающиеся проёмы).
        /// </summary>
        private List<CurtainWallDataDto> GetElements(Document doc)
        {
            Log("[CWPanelsCustomizer] GetElements START");

            var allCurtainWalls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Where(w => w != null && w.CurtainGrid != null)
                .ToList();

            Log($"[CWPanelsCustomizer] allCurtainWalls={allCurtainWalls.Count}");

            var allOpenings = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_GenericModel)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(fi => (fi.Symbol?.Family?.Name ?? "").Contains(OPENING_FAMILY_MARKER))
                .ToList();

            Log($"[CWPanelsCustomizer] allOpenings={allOpenings.Count}");

            var curtainWallsData = new List<CurtainWallDataDto>();
            var wallBboxesWorld = new Dictionary<ElementId, BoundingBoxXYZ>();

            foreach (Wall wall in allCurtainWalls)
            {
                BoundingBoxXYZ wallBboxWorld = wall.get_BoundingBox(null);

                Transform wallTransform = GetWallTransform(wall);
                Transform inverseTransform = wallTransform.Inverse;

                var cwDto = new CurtainWallDataDto
                {
                    Id = wall.Id,
                    CurtainWallElement = wall,
                    InverseTransform = inverseTransform
                };

                curtainWallsData.Add(cwDto);
                wallBboxesWorld[wall.Id] = wallBboxWorld;

                Log($"[CWPanelsCustomizer] wall Id={wall.Id.IntegerValue} bboxWorld={(wallBboxWorld == null ? "null" : "ok")}");
            }

            // Связь "проём -> витраж" по грубому пересечению BBox в мировой СК
            foreach (FamilyInstance opening in allOpenings)
            {
                BoundingBoxXYZ openingBboxWorld = opening.get_BoundingBox(null);
                if (openingBboxWorld == null)
                {
                    Log($"[CWPanelsCustomizer] opening Id={opening.Id.IntegerValue} bboxWorld=null skip");
                    continue;
                }

                CurtainWallDataDto host = null;

                foreach (CurtainWallDataDto cw in curtainWallsData)
                {
                    if (!wallBboxesWorld.TryGetValue(cw.Id, out BoundingBoxXYZ wallBboxWorld) || wallBboxWorld == null)
                        continue;

                    if (BoundingBoxesIntersectWorld(wallBboxWorld, openingBboxWorld))
                    {
                        host = cw;
                        break;
                    }
                }

                if (host == null)
                {
                    Log($"[CWPanelsCustomizer] opening Id={opening.Id.IntegerValue} intersects no wall");
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

                Log($"[CWPanelsCustomizer] opening Id={opening.Id.IntegerValue} assigned to wall Id={host.Id.IntegerValue}");
            }

            // Панели витража + BBox world/local
            foreach (CurtainWallDataDto cw in curtainWallsData)
            {
                CurtainGrid grid = cw.CurtainWallElement.CurtainGrid;
                if (grid == null) continue;

                ICollection<ElementId> panelIds = grid.GetPanelIds();
                Log($"[CWPanelsCustomizer] wall Id={cw.Id.IntegerValue} panelIds={panelIds.Count}");

                foreach (ElementId pid in panelIds)
                {
                    var panelFi = doc.GetElement(pid) as FamilyInstance;
                    if (panelFi == null)
                    {
                        Log($"[CWPanelsCustomizer] panel Id={pid.IntegerValue} not FamilyInstance skip");
                        continue;
                    }

                    BoundingBoxXYZ panelWorld = panelFi.get_BoundingBox(null);
                    if (panelWorld == null)
                    {
                        Log($"[CWPanelsCustomizer] panel Id={pid.IntegerValue} bboxWorld=null skip");
                        continue;
                    }

                    BoundingBoxXYZ panelLocal = TransformBoundingBoxToLocal(panelWorld, cw.InverseTransform);

                    cw.Panels.Add(new CurtainWallPanelDto
                    {
                        Id = panelFi.Id,
                        PanelElement = panelFi,
                        WorldBoundingBox = panelWorld,
                        LocalBoundingBox = panelLocal
                    });
                }

                Log($"[CWPanelsCustomizer] wall Id={cw.Id.IntegerValue} panelsFilled={cw.Panels.Count}");
            }

            var wallsInWork = curtainWallsData.Where(x => x.IntersectingOpenings.Any()).ToList();
            Log($"[CWPanelsCustomizer] wallsInWork={wallsInWork.Count}");

            Log("[CWPanelsCustomizer] GetElements END");
            return wallsInWork;
        }

        private int GetTotalOpeningsCount(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_GenericModel)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Count(fi => (fi.Symbol?.Family?.Name ?? "").Contains(OPENING_FAMILY_MARKER));
        }

        private int GetTotalCurtainWallsCount(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Count(w => w != null && w.CurtainGrid != null);
        }

        /// <summary>
        /// Локальная СК витража строится как в эталонном коде GetWallTransform.
        /// В этом плагине linkModelTransf.HasReflection не учитываем (как false), т.к. работаем в активном документе.
        /// </summary>
        private Transform GetWallTransform(Wall curWall)
        {
            Transform result = Transform.Identity;
            if (curWall == null) return result;

            var lc = curWall.Location as LocationCurve;
            if (lc == null)
            {
                Log($"[CWPanelsCustomizer] GetWallTransform wall Id={curWall.Id.IntegerValue} LocationCurve=null");
                return result;
            }

            var line = lc.Curve as Line;
            if (line == null)
            {
                Log($"[CWPanelsCustomizer] GetWallTransform wall Id={curWall.Id.IntegerValue} not Line");
                return result;
            }

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

            Log($"[CWPanelsCustomizer] GetWallTransform wall Id={curWall.Id.IntegerValue} Origin=({transf.Origin.X:F3},{transf.Origin.Y:F3},{transf.Origin.Z:F3})");
            return transf;
        }

        // -------------------------
        // Geometry / parameter utils
        // -------------------------

        private static void Log(string msg) => Debug.WriteLine(msg);

        private static XYZ GetCenter(BoundingBoxXYZ b) =>
            new XYZ((b.Min.X + b.Max.X) * 0.5, (b.Min.Y + b.Max.Y) * 0.5, (b.Min.Z + b.Max.Z) * 0.5);

        private static bool BoundingBoxesIntersectWorld(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            if (a == null || b == null) return false;
            return !(a.Max.X < b.Min.X || a.Min.X > b.Max.X ||
                     a.Max.Y < b.Min.Y || a.Min.Y > b.Max.Y ||
                     a.Max.Z < b.Min.Z || a.Min.Z > b.Max.Z);
        }

        private static bool BoundingBoxesIntersectLocal(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            if (a == null || b == null) return false;
            return !(a.Max.X < b.Min.X || a.Min.X > b.Max.X ||
                     a.Max.Y < b.Min.Y || a.Min.Y > b.Max.Y ||
                     a.Max.Z < b.Min.Z || a.Min.Z > b.Max.Z);
        }

        private static double OverlapX(BoundingBoxXYZ a, BoundingBoxXYZ b, double eps)
        {
            double min = Math.Max(a.Min.X, b.Min.X);
            double max = Math.Min(a.Max.X, b.Max.X);
            double o = max - min;
            return o > eps ? o : 0.0;
        }

        private static double OverlapZ(BoundingBoxXYZ a, BoundingBoxXYZ b, double eps)
        {
            double min = Math.Max(a.Min.Z, b.Min.Z);
            double max = Math.Min(a.Max.Z, b.Max.Z);
            double o = max - min;
            return o > eps ? o : 0.0;
        }

        private static bool TrySetParam(FamilyInstance fi, string name, double valFt)
        {
            if (fi == null) return false;

            Parameter p = fi.LookupParameter(name);
            if (p == null) return false;
            if (p.IsReadOnly) return false;

            try { p.Set(valFt); return true; }
            catch { return false; }
        }

        private static bool TrySetDouble(FamilyInstance fi, string paramName, double value, string tagForLog)
        {
            try
            {
                var p = fi.LookupParameter(paramName);
                if (p == null)
                {
                    Log($"{tagForLog} panelId={fi.Id.IntegerValue} param '{paramName}' not found");
                    return false;
                }
                if (p.IsReadOnly)
                {
                    Log($"{tagForLog} panelId={fi.Id.IntegerValue} param '{paramName}' is read-only");
                    return false;
                }
                p.Set(value);
                return true;
            }
            catch (Exception ex)
            {
                Log($"{tagForLog} panelId={fi.Id.IntegerValue} set '{paramName}' failed: {ex.Message}");
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

        /// <summary>
        /// Трансформация BoundingBoxXYZ по 8 углам в локальную СК витража.
        /// </summary>
        private BoundingBoxXYZ TransformBoundingBoxToLocal(BoundingBoxXYZ worldBbox, Transform inverseTransform)
        {
            if (worldBbox == null || inverseTransform == null) return null;

            double[] xs = { worldBbox.Min.X, worldBbox.Max.X };
            double[] ys = { worldBbox.Min.Y, worldBbox.Max.Y };
            double[] zs = { worldBbox.Min.Z, worldBbox.Max.Z };

            var pts = new List<XYZ>(8);
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

                return _doc.GetElement(symbolIds.First()) as FamilySymbol;
            }
            catch
            {
                return null;
            }
        }

        private static BoundingBoxXYZ ReduceBoundingBoxAroundCenter(BoundingBoxXYZ bbox, double reductionFactor)
        {
            if (bbox == null) return null;

            var center = GetCenter(bbox);

            double halfX = (bbox.Max.X - bbox.Min.X) * 0.5;
            double halfY = (bbox.Max.Y - bbox.Min.Y) * 0.5;
            double halfZ = (bbox.Max.Z - bbox.Min.Z) * 0.5;

            double newHalfX = halfX * reductionFactor;
            double newHalfY = halfY * reductionFactor;
            double newHalfZ = halfZ * reductionFactor;

            return new BoundingBoxXYZ
            {
                Min = new XYZ(center.X - newHalfX, center.Y - newHalfY, center.Z - newHalfZ),
                Max = new XYZ(center.X + newHalfX, center.Y + newHalfY, center.Z + newHalfZ)
            };
        }

        // -------------------------
        // 2D segment/rect helpers (XZ)
        // -------------------------

        private static List<FamilyInstance> GetHitPanelsBySegment2D(List<(FamilyInstance fi, BoundingBoxXYZ bbox)> panels, XYZ s1, XYZ s2)
        {
            var res = new List<FamilyInstance>();
            foreach (var p in panels)
            {
                if (SegmentIntersectsRect2D(s1, s2, p.bbox))
                    res.Add(p.fi);
            }
            return res;
        }

        private static bool SegmentIntersectsRect2D(XYZ p1, XYZ p2, BoundingBoxXYZ panelBox)
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

            return SegmentsIntersect2D(p1, p2, r1, r2) ||
                   SegmentsIntersect2D(p1, p2, r2, r3) ||
                   SegmentsIntersect2D(p1, p2, r3, r4) ||
                   SegmentsIntersect2D(p1, p2, r4, r1);
        }

        private static bool PointInRect2D(XYZ p, double minX, double maxX, double minZ, double maxZ) =>
            p.X >= minX && p.X <= maxX && p.Z >= minZ && p.Z <= maxZ;

        private static double Cross2D(XYZ a, XYZ b, XYZ c)
        {
            double abx = b.X - a.X;
            double abz = b.Z - a.Z;
            double acx = c.X - a.X;
            double acz = c.Z - a.Z;
            return abx * acz - abz * acx;
        }

        private static bool SegmentsIntersect2D(XYZ a, XYZ b, XYZ c, XYZ d)
        {
            const double EPS = 1e-9;

            double d1 = Cross2D(a, b, c);
            double d2 = Cross2D(a, b, d);
            double d3 = Cross2D(c, d, a);
            double d4 = Cross2D(c, d, b);

            bool proper = ((d1 > EPS && d2 < -EPS) || (d1 < -EPS && d2 > EPS)) &&
                          ((d3 > EPS && d4 < -EPS) || (d3 < -EPS && d4 > EPS));
            if (proper) return true;

            static bool Collinear(double val) => Math.Abs(val) <= EPS;

            static bool OnSeg(XYZ p, XYZ q, XYZ r)
            {
                return q.X >= Math.Min(p.X, r.X) - EPS && q.X <= Math.Max(p.X, r.X) + EPS &&
                       q.Z >= Math.Min(p.Z, r.Z) - EPS && q.Z <= Math.Max(p.Z, r.Z) + EPS;
            }

            if (Collinear(d1) && OnSeg(a, c, b)) return true;
            if (Collinear(d2) && OnSeg(a, d, b)) return true;
            if (Collinear(d3) && OnSeg(c, a, d)) return true;
            if (Collinear(d4) && OnSeg(c, b, d)) return true;

            return false;
        }
    }
}
