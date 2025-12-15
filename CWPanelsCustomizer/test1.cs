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
        public static string IS_NAME => "*Название плагина";
        public static string IS_DESCRIPTION => "*Что делает плагин?";
        public static string IS_TAB_NAME => "#BIM";
        public static string IS_IMAGE => "CWPanelsCustomizer.Images.a1.png";

        private UIDocument _uidoc;
        private Document _doc;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            _uidoc = commandData.Application.ActiveUIDocument;
            _doc = _uidoc.Document;

            Debug.WriteLine("[CWPanelsCustomizer] Execute START");

            using (TransactionGroup tg = new TransactionGroup(_doc, IS_NAME))
            {
                tg.Start();

                // 0) Сбор данных (первый метод)
                List<CurtainWallDataDto> data = GetElements(_doc);

                // 1) Сброс рядовых панелей (второй метод фасада)
                ResetRegularPanelsCutsForIntersectingOpenings(data);

                // 2) Настройка рядовых кассет (принимает DTO из первого метода)
                CalculateAndSetRegularPanelsCuts(data);


                // UI/статистика как часть фасада (пока оставляем тут)
                int totalOpenings = GetTotalOpeningsCount(_doc);
                int totalCurtainWalls = GetTotalCurtainWallsCount(_doc);
                int wallsInWork = data.Count;
                int totalAssignedOpenings = data.Sum(x => x.IntersectingOpenings.Count);

                Debug.WriteLine("[CWPanelsCustomizer] Summary:");
                Debug.WriteLine($"[CWPanelsCustomizer] Total openings: {totalOpenings}");
                Debug.WriteLine($"[CWPanelsCustomizer] Total curtain walls: {totalCurtainWalls}");
                Debug.WriteLine($"[CWPanelsCustomizer] Walls in work: {wallsInWork}");
                Debug.WriteLine($"[CWPanelsCustomizer] Total assigned openings: {totalAssignedOpenings}");

                //TaskDialog td = new TaskDialog("CWPanelsCustomizer");
                //td.MainIcon = TaskDialogIcon.TaskDialogIconInformation;
                //td.Title = "Статистика по витражам и проёмам";
                //td.TitleAutoPrefix = false;
                //td.MainInstruction = "Сбор данных завершён";
                //td.MainContent =
                //    $"Всего проёмов (#_Оконный проем_Прямоугольный): {totalOpenings}\n" +
                //    $"Всего витражей (OST_Walls с CurtainGrid): {totalCurtainWalls}\n" +
                //    $"Витражей в работе (пересекаются с проёмами): {wallsInWork}\n" +
                //    $"Связок витраж → проёмы (всего проёмов в работе): {totalAssignedOpenings}";
                //td.CommonButtons = TaskDialogCommonButtons.Ok;
                //td.DefaultButton = TaskDialogResult.Ok;
                //td.Show();

                tg.Assimilate();
            }

            Debug.WriteLine("[CWPanelsCustomizer] Execute END");
            return Result.Succeeded;
        }
        private void ResetRegularPanelsCutsForIntersectingOpenings(List<CurtainWallDataDto> data)
        {
            const string TAG = "[ResetRegularPanelsCutsForIntersectingOpenings]";
            const string REGULAR_PANEL_FAMILY = "КРСТ_НВФ_Рядовая_В3";
            System.Diagnostics.Debug.WriteLine($"{TAG} START");

            if (data == null || data.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"{TAG} data is null/empty -> END");
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

                        System.Diagnostics.Debug.WriteLine($"{TAG} wallId={wallDto.Id?.IntegerValue}, openings={openings.Count}, panels={panels.Count}");

                        if (openings.Count == 0 || panels.Count == 0)
                            continue;

                        foreach (var opening in openings)
                        {
                            if (opening == null || opening.OpeningElement == null)
                                continue;

                            openingsProcessed++;

                            var opLocal = opening.LocalBoundingBox;
                            if (opLocal == null)
                            {
                                System.Diagnostics.Debug.WriteLine($"{TAG} wallId={wallDto.Id.IntegerValue}, openingId={opening.Id.IntegerValue} opLocal=null skip");
                                continue;
                            }

                            System.Diagnostics.Debug.WriteLine($"{TAG} wallId={wallDto.Id.IntegerValue}, openingId={opening.Id.IntegerValue}, opLocalMin=({opLocal.Min.X:F4},{opLocal.Min.Y:F4},{opLocal.Min.Z:F4}), opLocalMax=({opLocal.Max.X:F4},{opLocal.Max.Y:F4},{opLocal.Max.Z:F4})");

                            // Находим панели, которые пересекаются с этим проёмом (в локальной СК витража)
                            var intersectingPanels = new List<CurtainWallPanelDto>();

                            foreach (var p in panels)
                            {
                                if (p == null || p.PanelElement == null)
                                    continue;

                                // Только рядовые панели
                                var fam = p.PanelElement.Symbol?.Family?.Name ?? "";
                                if (!fam.Contains(REGULAR_PANEL_FAMILY))
                                    continue;

                                var pLocal = p.LocalBoundingBox;
                                if (pLocal == null)
                                    continue;

                                if (BoundingBoxesIntersectLocal(opLocal, pLocal))
                                    intersectingPanels.Add(p);
                            }

                            System.Diagnostics.Debug.WriteLine($"{TAG} wallId={wallDto.Id.IntegerValue}, openingId={opening.Id.IntegerValue}, intersectingRegularPanels={intersectingPanels.Count}");

                            // Сброс подрезок у пересекающихся панелей
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

                                System.Diagnostics.Debug.WriteLine($"{TAG} wallId={wallDto.Id.IntegerValue}, openingId={opening.Id.IntegerValue}, panelId={fi.Id.IntegerValue}, reset(Подрезка={set1}, Подрезка_Верх={set2}, Подрезка_Низ={set3})");
                            }
                        }
                    }

                    t.Commit();
                }

                System.Diagnostics.Debug.WriteLine($"{TAG} END: wallsProcessed={wallsProcessed}, openingsProcessed={openingsProcessed}, panelsTouched={panelsTouched}, paramsSet={paramsSet}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"{TAG} ERROR: {ex}");
                TaskDialog.Show("ResetRegularPanelsCutsForIntersectingOpenings", ex.Message);
            }

            // --- локальные хелперы (инкапсулированы в методе) ---
            bool BoundingBoxesIntersectLocal(BoundingBoxXYZ a, BoundingBoxXYZ b)
            {
                if (a == null || b == null) return false;
                return !(a.Max.X < b.Min.X || a.Min.X > b.Max.X ||
                         a.Max.Y < b.Min.Y || a.Min.Y > b.Max.Y ||
                         a.Max.Z < b.Min.Z || a.Min.Z > b.Max.Z);
            }

            bool TrySetDouble(FamilyInstance fi, string paramName, double value)
            {
                try
                {
                    var p = fi.LookupParameter(paramName);
                    if (p == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"{TAG} panelId={fi.Id.IntegerValue} param '{paramName}' not found");
                        return false;
                    }
                    if (p.IsReadOnly)
                    {
                        System.Diagnostics.Debug.WriteLine($"{TAG} panelId={fi.Id.IntegerValue} param '{paramName}' is read-only");
                        return false;
                    }
                    // Ставим в feet (как и остальные расчёты/параметры в Revit)
                    p.Set(value);
                    return true;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"{TAG} panelId={fi.Id.IntegerValue} set '{paramName}' failed: {ex.Message}");
                    return false;
                }
            }
        }

        private void CalculateAndSetRegularPanelsCuts(List<CurtainWallDataDto> data)
        {
            System.Diagnostics.Debug.WriteLine("[CalculateAndSetRegularPanelsCuts] START");
            if (data == null || data.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[CalculateAndSetRegularPanelsCuts] data is null/empty -> END");
                return;
            }

            const double EPS = 1e-9;
            const double FEET_TO_MM = 304.8;

            // === Adjustments (mm) ===
            const double DELTA_MM = -43.0;
            const double VERTICAL_MM = 7.0;
            const double HORIZONTAL_MM = 55.0;

            double MmToFt(double mm) => mm / FEET_TO_MM;

            XYZ CenterOf(BoundingBoxXYZ b) => new XYZ((b.Min.X + b.Max.X) * 0.5, (b.Min.Y + b.Max.Y) * 0.5, (b.Min.Z + b.Max.Z) * 0.5);

            bool Intersects3D(BoundingBoxXYZ a, BoundingBoxXYZ b)
            {
                if (a == null || b == null) return false;
                return !(a.Max.X < b.Min.X || a.Min.X > b.Max.X || a.Max.Y < b.Min.Y || a.Min.Y > b.Max.Y || a.Max.Z < b.Min.Z || a.Min.Z > b.Max.Z);
            }

            double OverlapX(BoundingBoxXYZ a, BoundingBoxXYZ b)
            {
                double min = Math.Max(a.Min.X, b.Min.X);
                double max = Math.Min(a.Max.X, b.Max.X);
                double o = max - min;
                return o > EPS ? o : 0.0;
            }

            double OverlapZ(BoundingBoxXYZ a, BoundingBoxXYZ b)
            {
                double min = Math.Max(a.Min.Z, b.Min.Z);
                double max = Math.Min(a.Max.Z, b.Max.Z);
                double o = max - min;
                return o > EPS ? o : 0.0;
            }

            bool TrySetParam(FamilyInstance fi, string name, double valFt)
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
                catch { return false; }
            }

            int totalPanelsTouched = 0;
            int totalParamsSet = 0;
            int totalOpeningsProcessed = 0;

            using (Transaction t = new Transaction(_doc, "CW: Set regular panel cuts by openings (local bbox)"))
            {
                t.Start();

                foreach (var cw in data)
                {
                    if (cw == null || cw.CurtainWallElement == null)
                    {
                        System.Diagnostics.Debug.WriteLine("[CalculateAndSetRegularPanelsCuts] skip: null cw or wall");
                        continue;
                    }

                    var wallId = cw.CurtainWallElement.Id.IntegerValue;
                    var openings = cw.IntersectingOpenings ?? new List<OpeningModelDto>();
                    var panelsAll = cw.Panels ?? new List<CurtainWallPanelDto>();

                    var regularPanels = panelsAll
                        .Where(p => p != null && p.PanelElement != null && p.PanelElement.Symbol != null && p.PanelElement.Symbol.Family != null)
                        .Where(p => p.PanelElement.Symbol.Family.Name == "КРСТ_НВФ_Рядовая_В3")
                        .Where(p => p.LocalBoundingBox != null)
                        .ToList();

                    System.Diagnostics.Debug.WriteLine($"[CalculateAndSetRegularPanelsCuts] wallId={wallId}, openings={openings.Count}, regularPanels={regularPanels.Count}");

                    if (openings.Count == 0 || regularPanels.Count == 0)
                        continue;

                    foreach (var op in openings)
                    {
                        if (op == null || op.OpeningElement == null || op.LocalBoundingBox == null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[CalculateAndSetRegularPanelsCuts] wallId={wallId}: skip opening null/bbox null");
                            continue;
                        }

                        totalOpeningsProcessed++;
                        var opId = op.OpeningElement.Id.IntegerValue;
                        var opBox = op.LocalBoundingBox;
                        var opC = CenterOf(opBox);

                        System.Diagnostics.Debug.WriteLine($"[CalculateAndSetRegularPanelsCuts] wallId={wallId}, openingId={opId}, opLocalMin=({opBox.Min.X:F4},{opBox.Min.Y:F4},{opBox.Min.Z:F4}), opLocalMax=({opBox.Max.X:F4},{opBox.Max.Y:F4},{opBox.Max.Z:F4})");

                        var candidatePanels = regularPanels
                            .Where(p => Intersects3D(opBox, p.LocalBoundingBox))
                            .ToList();

                        System.Diagnostics.Debug.WriteLine($"[CalculateAndSetRegularPanelsCuts] wallId={wallId}, openingId={opId}, candidatePanels={candidatePanels.Count}");

                        if (candidatePanels.Count == 0)
                            continue;

                        int panelsTouchedThisOpening = 0;
                        int paramsSetThisOpening = 0;

                        foreach (var pdto in candidatePanels)
                        {
                            var panel = pdto.PanelElement;
                            var pId = panel.Id.IntegerValue;
                            var pBox = pdto.LocalBoundingBox;
                            if (pBox == null) continue;

                            var pC = CenterOf(pBox);
                            double dx = pC.X - opC.X;
                            double dz = pC.Z - opC.Z;

                            string side;
                            string paramName;
                            double baseValueFt;
                            double adjustedValueFt;

                            if (Math.Abs(dz) >= Math.Abs(dx))
                            {
                                // Vertical relation: Top/Bottom
                                if (dz > 0)
                                {
                                    side = "Top";
                                    paramName = "Подрезка_Низ";
                                    baseValueFt = OverlapZ(opBox, pBox);
                                    adjustedValueFt = baseValueFt + MmToFt(VERTICAL_MM + DELTA_MM); // Подрезка_Низ=Вертикальная+Подрезка_Низ+Дельта
                                }
                                else
                                {
                                    side = "Bottom";
                                    paramName = "Подрезка_Верх";
                                    baseValueFt = OverlapZ(opBox, pBox);
                                    adjustedValueFt = baseValueFt - MmToFt(VERTICAL_MM) + MmToFt(DELTA_MM); // Подрезка_Верх=Подрезка_Верх-Вертикальная+Дельта
                                }
                            }
                            else
                            {
                                // Horizontal relation: Left/Right
                                side = dx < 0 ? "Left" : "Right";
                                paramName = "Подрезка";
                                baseValueFt = OverlapX(opBox, pBox);
                                adjustedValueFt = baseValueFt - MmToFt(HORIZONTAL_MM) + MmToFt(DELTA_MM); // Подрезка=Подрезка-Горизонтальная+Дельта
                            }

                            if (baseValueFt <= EPS)
                            {
                                System.Diagnostics.Debug.WriteLine($"[CalculateAndSetRegularPanelsCuts] wallId={wallId}, openingId={opId}, panelId={pId}, side={side}: overlap=0 -> skip");
                                continue;
                            }

                            if (adjustedValueFt <= EPS)
                            {
                                System.Diagnostics.Debug.WriteLine($"[CalculateAndSetRegularPanelsCuts] wallId={wallId}, openingId={opId}, panelId={pId}, side={side}: adjusted<=0 (baseFt={baseValueFt:F6}) -> skip");
                                continue;
                            }

                            bool setOk = TrySetParam(panel, paramName, adjustedValueFt);

                            System.Diagnostics.Debug.WriteLine(
                                $"[CalculateAndSetRegularPanelsCuts] wallId={wallId}, openingId={opId}, panelId={pId}, side={side}, param={paramName}, " +
                                $"baseFt={baseValueFt:F6} ({baseValueFt * FEET_TO_MM:F1}mm), adjFt={adjustedValueFt:F6} ({adjustedValueFt * FEET_TO_MM:F1}mm), set={setOk}");

                            if (setOk)
                            {
                                panelsTouchedThisOpening++;
                                paramsSetThisOpening++;
                            }
                        }

                        totalPanelsTouched += panelsTouchedThisOpening;
                        totalParamsSet += paramsSetThisOpening;

                        System.Diagnostics.Debug.WriteLine($"[CalculateAndSetRegularPanelsCuts] wallId={wallId}, openingId={opId}: touchedPanels={panelsTouchedThisOpening}, paramsSet={paramsSetThisOpening}");
                    }
                }

                t.Commit();
            }

            System.Diagnostics.Debug.WriteLine($"[CalculateAndSetRegularPanelsCuts] END: openingsProcessed={totalOpeningsProcessed}, panelsTouched={totalPanelsTouched}, paramsSet={totalParamsSet}");
        }


        /// <summary>
        /// Первый метод фасада: собирает витражи, проёмы и панели,
        /// строит inverse transform витража и преобразует BBox в локальную СК витража.
        /// Возвращает ТОЛЬКО витражи "в работе" (у которых есть пересекающиеся проёмы).
        /// </summary>
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

            // Связь "проём -> витраж" по грубому пересечению BBox в мировой СК
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

            // Панели витража + BBox world/local
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
                    {
                        Debug.WriteLine($"[CWPanelsCustomizer] panel Id={pid.IntegerValue} not FamilyInstance skip");
                        continue;
                    }

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
                        LocalBoundingBox = panelLocal
                    });
                }

                Debug.WriteLine($"[CWPanelsCustomizer] wall Id={cw.Id.IntegerValue} panelsFilled={cw.Panels.Count}");
            }

            // "В работу" берём только витражи с проёмами
            List<CurtainWallDataDto> wallsInWork = curtainWallsData.Where(x => x.IntersectingOpenings.Any()).ToList();
            Debug.WriteLine($"[CWPanelsCustomizer] wallsInWork={wallsInWork.Count}");

            Debug.WriteLine("[CWPanelsCustomizer] GetElements END");
            return wallsInWork;
        }

        /// <summary>
        /// Заглушка следующего шага фасада.
        /// Дальше тут появится логика обработки данных (поиск панелей для каждого проёма и т.д.).
        /// </summary>
        private void ProcessCurtainWalls(List<CurtainWallDataDto> data)
        {
            Debug.WriteLine("[CWPanelsCustomizer] ProcessCurtainWalls START (stub)");
            Debug.WriteLine($"[CWPanelsCustomizer] ProcessCurtainWalls input walls={data?.Count ?? 0}");

            if (data == null || data.Count == 0)
            {
                Debug.WriteLine("[CWPanelsCustomizer] ProcessCurtainWalls: nothing to process");
                Debug.WriteLine("[CWPanelsCustomizer] ProcessCurtainWalls END (stub)");
                return;
            }

            foreach (CurtainWallDataDto cw in data)
            {
                Debug.WriteLine($"[CWPanelsCustomizer] wall Id={cw.Id.IntegerValue}, openings={cw.IntersectingOpenings.Count}, panels={cw.Panels.Count}");
            }

            Debug.WriteLine("[CWPanelsCustomizer] ProcessCurtainWalls END (stub)");
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

        /// <summary>
        /// Локальная СК витража строится как в эталонном коде GetWallTransform.
        /// В этом плагине linkModelTransf.HasReflection не учитываем (как false), т.к. работаем в активном документе.
        /// </summary>
        private Transform GetWallTransform(Wall curWall)
        {
            Transform result = Transform.Identity;
            if (curWall == null) return result;

            LocationCurve lc = curWall.Location as LocationCurve;
            if (lc == null)
            {
                Debug.WriteLine($"[CWPanelsCustomizer] GetWallTransform wall Id={curWall.Id.IntegerValue} LocationCurve=null");
                return result;
            }

            Line line = lc.Curve as Line;
            if (line == null)
            {
                Debug.WriteLine($"[CWPanelsCustomizer] GetWallTransform wall Id={curWall.Id.IntegerValue} not Line");
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

        /// <summary>
        /// Трансформация BoundingBoxXYZ по 8 углам в локальную СК витража.
        /// </summary>
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
    }
}
