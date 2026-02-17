using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using CWPanelsCustomizer.Helpers;

namespace CWPanelsCustomizer
{
    [Transaction(TransactionMode.Manual)]
    public class KrMullionPlacement : IExternalCommand
    {
        public static string IS_TAB_NAME => "BIM";
        public static string IS_NAME => "Разместить стойки по витражу";
        public static string IS_DESCRIPTION => "Размещение семейства стоек по вертикальным линиям витража на поверхности стены";
        public static string IS_IMAGE => "CWPanelsCustomizer.Images.a1.png";

        private const double FEET_TO_MM = 304.8;
        private const double RACK_HEIGHT_MM = 3000.0;
        private const double RACK_HEIGHT_FT = RACK_HEIGHT_MM / FEET_TO_MM;
        private const double RACK_GAP_MM = 10.0;
        private const double RACK_GAP_FT = RACK_GAP_MM / FEET_TO_MM;
        private const double RACK_START_OFFSET_MM = 5.0;
        private const double RACK_START_OFFSET_FT = RACK_START_OFFSET_MM / FEET_TO_MM;
        private const double RACK_MIN_HEIGHT_MM = 1200.0;
        private const double RACK_MIN_HEIGHT_FT = RACK_MIN_HEIGHT_MM / FEET_TO_MM;
        private const double OPENING_TOP_OFFSET_MM = 95.0;
        private const double OPENING_TOP_OFFSET_FT = OPENING_TOP_OFFSET_MM / FEET_TO_MM;

        private SphereByPoint _sphereByPoint;
        private UIDocument _uidoc;
        private Document _doc;
        private RevitLogger _logger;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            _uidoc = commandData.Application.ActiveUIDocument;
            _doc = _uidoc.Document;
            _sphereByPoint = new SphereByPoint(_doc);

            _logger = RevitLogger.GetLogger(_doc);
            _logger.BeginSession(IS_NAME, _doc.Title);

            using (TransactionGroup tg = new TransactionGroup(_doc, IS_NAME))
            {
                tg.Start();

                try
                {
                    Method();
                    tg.Assimilate();
                    _logger.EndSession("Succeeded");
                }
                catch (Exception ex)
                {
                    _logger.Error("FAILED", ex);
                    _logger.EndSession("Failed");
                    throw;
                }
            }

            return Result.Succeeded;
        }
        /// <summary>
        /// Осноснвая логика команды
        /// </summary>
        /// <exception cref="InvalidOperationException"></exception>
        private void Method()
        {
            Stopwatch sw = Stopwatch.StartNew();

            View activeView = _doc.ActiveView;
            View3D view3D = activeView as View3D;
            if (view3D == null)
            {
                _logger.Error("Активный вид не является 3D видом. ViewType=" + activeView.ViewType);
                throw new InvalidOperationException("Команда должна запускаться на 3D виде.");
            }

            SketchPlane sketchPlane = view3D.SketchPlane;
            if (sketchPlane == null)
            {
                _logger.Error("На 3D виде не задана рабочая плоскость (SketchPlane == null).");
                throw new InvalidOperationException("На активном 3D виде должна быть заранее настроена рабочая плоскость.");
            }

            Plane workPlane = sketchPlane.GetPlane();
            _logger.Info("WorkPlane: Origin=" + FormatXyz(workPlane.Origin) + " Normal=" + FormatXyz(workPlane.Normal));

            const string familyName = "КРСТ_НВФ_ZIAS_Стойка с кронштейнами в сборе_В2";
            const string symbolName = "Тип 1";
            const string oldFamilyName = "КРСТ_НВФ_ZIAS_Массив стоек с кронштейнами_В2";

            FamilySymbol symbol = FindFamilySymbolByNames(_doc, familyName, symbolName);
            if (symbol == null)
            {
                _logger.Error("Не найден FamilySymbol. FamilyName='" + familyName + "', TypeName='" + symbolName + "'.");
                throw new InvalidOperationException("Не найдено семейство/тип: " + familyName + " : " + symbolName);
            }

            _logger.LogElement("FamilySymbol", symbol.Id.IntegerValue, symbol.FamilyName, symbol.Name);

            List<Wall> allCurtainWalls = new FilteredElementCollector(_doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Where(w => w != null && w.CurtainGrid != null)
                .ToList();

            _logger.Info("CurtainWalls found: " + allCurtainWalls.Count);

            Wall targetWall = FindNearestCurtainWallToPlane(allCurtainWalls, workPlane);
            if (targetWall == null)
            {
                _logger.Error("Не найден витраж (CurtainWall) рядом с рабочей плоскостью.");
                throw new InvalidOperationException("Не найден витраж (CurtainWall) рядом с рабочей плоскостью.");
            }

            _logger.LogElement("Target CurtainWall", targetWall.Id.IntegerValue, extraInfo: "Name=" + targetWall.Name);

            CurtainGrid curtainGrid = targetWall.CurtainGrid;
            if (curtainGrid == null)
            {
                _logger.Error("У выбранного витража CurtainGrid == null. WallId=" + targetWall.Id.IntegerValue);
                throw new InvalidOperationException("У выбранного витража отсутствует CurtainGrid.");
            }

            ICollection<ElementId> uIds = curtainGrid.GetUGridLineIds();
            ICollection<ElementId> vIds = curtainGrid.GetVGridLineIds();
            int uCount = uIds != null ? uIds.Count : 0;
            int vCount = vIds != null ? vIds.Count : 0;
            _logger.Info("CurtainGrid lines: U=" + uCount + " V=" + vCount + " total=" + (uCount + vCount));

            List<CurtainGridLine> allGridLines = GetAllCurtainGridLines(_doc, curtainGrid);

            List<CurtainGridLine> verticalGridLines = allGridLines
                .Where(gl => gl != null)
                .Where(gl =>
                {
                    Curve c = GetGridLineCurve(gl);
                    if (c == null) return false;
                    XYZ dir = GetCurveDirection(c);
                    double verticality = Math.Abs(dir.DotProduct(XYZ.BasisZ));
                    return verticality >= 0.99;
                })
                .ToList();

            _logger.Info("Vertical grid lines: " + verticalGridLines.Count);

            if (verticalGridLines.Count == 0)
            {
                _logger.Warn("Нет вертикальных линий сетки (verticality >= 0.99). Стойки не будут размещены.");
                return;
            }

            // Удалить все существующие экземпляры нового семейства и остатки старого
            int deletedOld = DeleteExistingInstancesByFamilyName(_doc, oldFamilyName);
            int deleted = DeleteExistingInstances(_doc, symbol);
            int totalDeleted = deleted + deletedOld;
            if (totalDeleted > 0)
            {
                _logger.Info("Deleted: new_family=" + deleted + " old_family=" + deletedOld + " total=" + totalDeleted);
            }

            using (Transaction t = new Transaction(_doc, "Place racks along vertical curtain grid (from bottom)"))
            {
                t.Start();

                FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new DuplicateInstancesWarningSuppressor(_logger));
                t.SetFailureHandlingOptions(fho);

                if (!symbol.IsActive)
                {
                    _logger.Info("Symbol не активен -> Activate()");
                    symbol.Activate();
                    _doc.Regenerate();
                }

                // --- Этап 1: Сбор данных ---
                List<int> origIndices = new List<int>();
                List<CurtainGridLine> validGridLines = new List<CurtainGridLine>();
                List<XYZ> bottomPoints = new List<XYZ>();

                for (int i = 0; i < verticalGridLines.Count; i++)
                {
                    CurtainGridLine gridLine = verticalGridLines[i];
                    Curve curve = GetGridLineCurve(gridLine);
                    if (curve == null)
                    {
                        _logger.Warn("GridLine[" + i + "] Id=" + gridLine.Id.IntegerValue + ": FullCurve is null -> skip");
                        continue;
                    }
                    origIndices.Add(i);
                    validGridLines.Add(gridLine);
                    bottomPoints.Add(GetCurveBottomPoint(curve));
                }

                // --- Этап 2: Сортировка и snap переходных Z ---
                XYZ wallHorizontal = workPlane.Normal.CrossProduct(XYZ.BasisZ);
                if (!wallHorizontal.IsZeroLength())
                    wallHorizontal = wallHorizontal.Normalize();
                else
                    wallHorizontal = XYZ.BasisX;

                int[] sortOrder = Enumerable.Range(0, bottomPoints.Count).ToArray();
                Array.Sort(sortOrder, (a, b) => wallHorizontal.DotProduct(bottomPoints[a])
                    .CompareTo(wallHorizontal.DotProduct(bottomPoints[b])));

                double[] sortedZ = sortOrder.Select(idx => bottomPoints[idx].Z).ToArray();
                double[] snappedSortedZ = SnapTransitionBottomZ(sortedZ);

                double[] snappedZ = new double[bottomPoints.Count];
                for (int s = 0; s < sortOrder.Length; s++)
                    snappedZ[sortOrder[s]] = snappedSortedZ[s];

                // --- Этап 2b: Верхний обрез из FullCurve линий сетки (обрезаны по контуру витража) ---
                double[] actualTopZ = new double[validGridLines.Count];
                for (int i = 0; i < validGridLines.Count; i++)
                {
                    Curve curve = GetGridLineCurve(validGridLines[i]);
                    actualTopZ[i] = curve != null ? GetCurveTopPoint(curve).Z : 0;
                }

                // --- Этап 2c: Сбор данных о проёмах для подрезки стоек ---
                const string openingFamilyName = "#_Оконный проем_Прямоугольный";
                List<double[]> openingDataList = CollectWindowOpenings(openingFamilyName, wallHorizontal);
                const double OPENING_MATCH_TOLERANCE_FT = 50.0 / FEET_TO_MM;

                // --- Этап 3: Размещение стоек по свободным сегментам (с учётом проёмов) ---
                int created = 0;

                for (int i = 0; i < validGridLines.Count; i++)
                {
                    int origIdx = origIndices[i];
                    XYZ bottomPt = bottomPoints[i];
                    double bottomZ = snappedZ[i] + RACK_START_OFFSET_FT;
                    double topZ = actualTopZ[i];
                    bool wasSnapped = Math.Abs(snappedZ[i] - bottomPt.Z) > 0.001;
                    string snapInfo = wasSnapped ? " snap=" + FormatFeetMm(snappedZ[i]) : "";

                    if (topZ - bottomZ < 0.01)
                    {
                        _logger.Warn("GridLine[" + origIdx + "]: totalHeight too small, skip");
                        continue;
                    }

                    // Найти проёмы, пересекающие эту линию сетки
                    double gridLineHPos = wallHorizontal.DotProduct(bottomPt);
                    List<double[]> matchingOpenings = FindOpeningsForGridLine(
                        openingDataList, gridLineHPos, OPENING_MATCH_TOLERANCE_FT);
                    List<double[]> freeSegments = GetFreeSegments(bottomZ, topZ, matchingOpenings);

                    _logger.Info("GridLine[" + origIdx + "] Id=" + validGridLines[i].Id.IntegerValue
                        + snapInfo
                        + " bottomZ=" + FormatFeetMm(bottomZ)
                        + " topZ=" + FormatFeetMm(topZ)
                        + " openings=" + matchingOpenings.Count
                        + " segments=" + freeSegments.Count);

                    if (matchingOpenings.Count > 0)
                    {
                        foreach (var seg in freeSegments)
                        {
                            _logger.Info("    FreeSegment: " + FormatFeetMm(seg[0]) + ".." + FormatFeetMm(seg[1])
                                + " h=" + FormatFeetMm(seg[1] - seg[0]));
                        }
                    }

                    int pieceIdx = 0;

                    foreach (var segment in freeSegments)
                    {
                        double segBottom = segment[0];
                        double segTop = segment[1];
                        if (segTop - segBottom < 0.01) continue;

                        double currentZ = segBottom;

                        while (currentZ < segTop - 0.001)
                        {
                            double remaining = segTop - currentZ;
                            if (remaining < 0.001) break;

                            double pieceHeightFt;
                            bool isLast;
                            string pieceTag;

                            if (remaining <= RACK_HEIGHT_FT + 0.001)
                            {
                                pieceHeightFt = remaining;
                                isLast = true;
                                pieceTag = " [last]";
                            }
                            else
                            {
                                double afterFull = remaining - RACK_HEIGHT_FT - RACK_GAP_FT;

                                if (afterFull < RACK_MIN_HEIGHT_FT - 0.001)
                                {
                                    double adjusted = remaining - RACK_GAP_FT - RACK_MIN_HEIGHT_FT;

                                    if (adjusted >= RACK_MIN_HEIGHT_FT - 0.001)
                                    {
                                        pieceHeightFt = adjusted;
                                        isLast = false;
                                        pieceTag = " [min_guard_trim]";
                                    }
                                    else
                                    {
                                        pieceHeightFt = remaining;
                                        isLast = true;
                                        pieceTag = " [last_merged]";
                                    }
                                }
                                else
                                {
                                    pieceHeightFt = RACK_HEIGHT_FT;
                                    isLast = false;
                                    pieceTag = "";
                                }
                            }

                            if (pieceHeightFt < 0.001) break;

                            XYZ placementPt = new XYZ(bottomPt.X, bottomPt.Y, currentZ);
                            XYZ projected = ProjectPointToPlane(placementPt, workPlane);

                            FamilyInstance inst = _doc.Create.NewFamilyInstance(
                                projected, symbol, sketchPlane, StructuralType.NonStructural);
                            bool paramSet = TrySetParameter(inst, "Профиль_Длина", pieceHeightFt);

                            _logger.Info("  piece[" + pieceIdx + "] Id=" + inst.Id.IntegerValue
                                + " Z=" + FormatFeetMm(currentZ)
                                + " Профиль_Длина=" + FormatFeetMm(pieceHeightFt)
                                + pieceTag
                                + (paramSet ? " SET=OK" : " SET=FAIL")
                                + " Pos=" + FormatXyz(projected));

                            created++;
                            pieceIdx++;

                            if (isLast) break;
                            currentZ += pieceHeightFt + RACK_GAP_FT;
                        }
                    }
                }

                _logger.LogSummary("Placement result", ("Deleted", totalDeleted), ("Created", created));

                t.Commit();
            }

            sw.Stop();
            _logger.Info("Execution time: " + sw.ElapsedMilliseconds + "ms");
        }

        private Wall FindNearestCurtainWallToPlane(List<Wall> curtainWalls, Plane plane)
        {
            if (curtainWalls == null || curtainWalls.Count == 0) return null;
            if (plane == null) return null;

            Wall best = null;
            double bestDist = double.MaxValue;

            foreach (Wall w in curtainWalls)
            {
                BoundingBoxXYZ bb = w.get_BoundingBox(null);
                if (bb == null || bb.Min == null || bb.Max == null) continue;

                XYZ center = (bb.Min + bb.Max) * 0.5;
                double signed = plane.Normal.DotProduct(center - plane.Origin);
                double dist = Math.Abs(signed);

                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = w;
                }
            }

            if (best != null)
            {
                _logger.Info("Nearest curtain wall chosen: Id=" + best.Id.IntegerValue + ", DistToPlaneFt=" + bestDist.ToString("F6"));
            }

            return best;
        }

        private List<CurtainGridLine> GetAllCurtainGridLines(Document doc, CurtainGrid grid)
        {
            List<CurtainGridLine> result = new List<CurtainGridLine>();
            if (doc == null || grid == null) return result;

            ICollection<ElementId> uIds = grid.GetUGridLineIds();
            ICollection<ElementId> vIds = grid.GetVGridLineIds();

            if (uIds != null)
            {
                foreach (ElementId id in uIds)
                {
                    CurtainGridLine gl = doc.GetElement(id) as CurtainGridLine;
                    if (gl != null) result.Add(gl);
                }
            }

            if (vIds != null)
            {
                foreach (ElementId id in vIds)
                {
                    CurtainGridLine gl = doc.GetElement(id) as CurtainGridLine;
                    if (gl != null) result.Add(gl);
                }
            }

            return result;
        }

        private Curve GetGridLineCurve(CurtainGridLine gridLine)
        {
            if (gridLine == null) return null;
            try { return gridLine.FullCurve; } catch { return null; }
        }

        private XYZ GetCurveBottomPoint(Curve curve)
        {
            if (curve == null) return XYZ.Zero;
            XYZ p0 = curve.GetEndPoint(0);
            XYZ p1 = curve.GetEndPoint(1);
            return p0.Z <= p1.Z ? p0 : p1;
        }

        private XYZ GetCurveTopPoint(Curve curve)
        {
            if (curve == null) return XYZ.Zero;
            XYZ p0 = curve.GetEndPoint(0);
            XYZ p1 = curve.GetEndPoint(1);
            return p0.Z >= p1.Z ? p0 : p1;
        }

        private double[] ComputeTopZFromPanels(CurtainGrid grid, List<XYZ> bottomPoints,
            int[] sortOrder, XYZ wallHorizontal)
        {
            // Собрать topZ всех панелей с их горизонтальной позицией
            ICollection<ElementId> panelIds = grid.GetPanelIds();
            var panelTops = new List<KeyValuePair<double, double>>();

            foreach (ElementId pid in panelIds)
            {
                Element panel = _doc.GetElement(pid);
                if (panel == null) continue;
                BoundingBoxXYZ bb = panel.get_BoundingBox(null);
                if (bb == null) continue;
                XYZ center = (bb.Min + bb.Max) * 0.5;
                panelTops.Add(new KeyValuePair<double, double>(
                    wallHorizontal.DotProduct(center), bb.Max.Z));
            }

            _logger.Info("Panel BoundingBoxes collected: " + panelTops.Count + " of " + panelIds.Count + " panels");

            // Горизонтальные позиции линий сетки в порядке сортировки
            double[] sortedHPos = new double[sortOrder.Length];
            for (int s = 0; s < sortOrder.Length; s++)
                sortedHPos[s] = wallHorizontal.DotProduct(bottomPoints[sortOrder[s]]);

            // Для каждой линии найти maxTopZ панелей в соседних колонках
            double[] sortedTopZ = new double[sortOrder.Length];
            for (int s = 0; s < sortOrder.Length; s++)
            {
                double leftH = s > 0 ? sortedHPos[s - 1] : sortedHPos[s] - 100;
                double rightH = s < sortOrder.Length - 1 ? sortedHPos[s + 1] : sortedHPos[s] + 100;

                double maxZ = 0;
                foreach (var kv in panelTops)
                {
                    if (kv.Key >= leftH && kv.Key <= rightH)
                        maxZ = Math.Max(maxZ, kv.Value);
                }
                sortedTopZ[s] = maxZ;
            }

            // Обратная проекция в исходный порядок
            double[] result = new double[bottomPoints.Count];
            for (int s = 0; s < sortOrder.Length; s++)
                result[sortOrder[s]] = sortedTopZ[s];

            return result;
        }

        private double GetGridLineActualTopZ(CurtainGridLine gridLine)
        {
            double maxZ = double.MinValue;
            try
            {
                CurveArray segments = gridLine.AllSegmentCurves;
                if (segments != null && segments.Size > 0)
                {
                    foreach (Curve seg in segments)
                    {
                        maxZ = Math.Max(maxZ, Math.Max(seg.GetEndPoint(0).Z, seg.GetEndPoint(1).Z));
                    }
                }
            }
            catch { /* AllSegmentCurves может бросить исключение */ }

            if (maxZ > double.MinValue) return maxZ;

            // Fallback на FullCurve
            try
            {
                Curve fc = gridLine.FullCurve;
                if (fc != null)
                    return Math.Max(fc.GetEndPoint(0).Z, fc.GetEndPoint(1).Z);
            }
            catch { }

            return 0;
        }

        private double[] SnapTransitionBottomZ(double[] sortedBottomZ)
        {
            double[] snapped = (double[])sortedBottomZ.Clone();
            if (snapped.Length < 3) return snapped;

            for (int i = 1; i < snapped.Length - 1; i++)
            {
                double left = sortedBottomZ[i - 1];
                double right = sortedBottomZ[i + 1];
                double current = sortedBottomZ[i];
                double lo = Math.Min(left, right);
                double hi = Math.Max(left, right);

                if (current > lo + 0.001 && current < hi - 0.001)
                {
                    double distLeft = Math.Abs(current - left);
                    double distRight = Math.Abs(current - right);
                    snapped[i] = distLeft <= distRight ? left : right;
                    _logger.Info("Snap transition: sortedIdx=" + i
                        + " Z=" + FormatFeetMm(current) + " → " + FormatFeetMm(snapped[i]));
                }
            }

            return snapped;
        }

        private List<double[]> CollectWindowOpenings(string familyName, XYZ wallHorizontal)
        {
            var result = new List<double[]>();

            List<FamilyInstance> openings = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.Symbol != null &&
                    string.Equals(fi.Symbol.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            _logger.Info("Window openings ('" + familyName + "'): " + openings.Count);

            foreach (var opening in openings)
            {
                BoundingBoxXYZ bb = opening.get_BoundingBox(null);
                if (bb == null) continue;

                LocationPoint locPt = opening.Location as LocationPoint;
                if (locPt == null) continue;

                double hPos = wallHorizontal.DotProduct(locPt.Point);
                result.Add(new[] { hPos, bb.Min.Z, bb.Max.Z });

                _logger.Info("  Opening Id=" + opening.Id.IntegerValue
                    + " hPos=" + FormatFeetMm(hPos)
                    + " Z=" + FormatFeetMm(bb.Min.Z) + ".." + FormatFeetMm(bb.Max.Z));
            }

            return result;
        }

        private List<double[]> FindOpeningsForGridLine(
            List<double[]> allOpenings, double gridLineHPos, double tolerance)
        {
            var result = new List<double[]>();
            foreach (var opening in allOpenings)
            {
                if (Math.Abs(opening[0] - gridLineHPos) <= tolerance)
                {
                    result.Add(opening);
                }
            }
            result.Sort((a, b) => a[1].CompareTo(b[1]));
            return result;
        }

        private List<double[]> GetFreeSegments(double bottomZ, double topZ, List<double[]> sortedOpenings)
        {
            var segments = new List<double[]>();
            if (sortedOpenings == null || sortedOpenings.Count == 0)
            {
                segments.Add(new[] { bottomZ, topZ });
                return segments;
            }

            double currentStart = bottomZ;

            foreach (var opening in sortedOpenings)
            {
                double openingMin = opening[1];
                double openingMax = opening[2];

                if (openingMax <= currentStart + 0.001) continue;

                if (openingMin > currentStart + 0.001)
                {
                    double segEnd = Math.Min(openingMin, topZ);
                    if (segEnd > currentStart + 0.001)
                    {
                        segments.Add(new[] { currentStart, segEnd });
                    }
                }

                currentStart = Math.Max(currentStart, openingMax - OPENING_TOP_OFFSET_FT);
                if (currentStart >= topZ - 0.001) break;
            }

            if (currentStart < topZ - 0.001)
            {
                segments.Add(new[] { currentStart, topZ });
            }

            return segments;
        }

        private XYZ GetCurveDirection(Curve curve)
        {
            if (curve == null) return XYZ.BasisX;

            XYZ p0 = curve.GetEndPoint(0);
            XYZ p1 = curve.GetEndPoint(1);
            XYZ v = p1 - p0;

            if (v.IsZeroLength()) return XYZ.BasisX;
            return v.Normalize();
        }

        private XYZ ProjectPointToPlane(XYZ point, Plane plane)
        {
            if (point == null || plane == null) return point;
            XYZ v = point - plane.Origin;
            double d = plane.Normal.DotProduct(v);
            return point - plane.Normal * d;
        }

        private int DeleteExistingInstances(Document doc, FamilySymbol symbol)
        {
            if (doc == null || symbol == null) return 0;

            ICollection<ElementId> toDelete = new FilteredElementCollector(doc)
                .WherePasses(new FamilyInstanceFilter(doc, symbol.Id))
                .ToElementIds();

            if (toDelete.Count == 0) return 0;

            using (Transaction t = new Transaction(doc, "Delete existing rack instances"))
            {
                t.Start();
                doc.Delete(toDelete);
                t.Commit();
            }

            return toDelete.Count;
        }

        private int DeleteExistingInstancesByFamilyName(Document doc, string familyName)
        {
            if (doc == null || string.IsNullOrEmpty(familyName)) return 0;

            List<ElementId> toDelete = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.Symbol != null &&
                    string.Equals(fi.Symbol.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
                .Select(fi => fi.Id)
                .ToList();

            if (toDelete.Count == 0) return 0;

            using (Transaction t = new Transaction(doc, "Delete old rack instances"))
            {
                t.Start();
                doc.Delete(toDelete);
                t.Commit();
            }

            return toDelete.Count;
        }

        private class DuplicateInstancesWarningSuppressor : IFailuresPreprocessor
        {
            private readonly RevitLogger _logger;

            public DuplicateInstancesWarningSuppressor(RevitLogger logger)
            {
                _logger = logger;
            }

            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                IList<FailureMessageAccessor> failures = failuresAccessor.GetFailureMessages();
                if (failures == null || failures.Count == 0) return FailureProcessingResult.Continue;

                Dictionary<string, int> warningCounts = new Dictionary<string, int>();

                foreach (FailureMessageAccessor fma in failures)
                {
                    if (fma == null) continue;

                    FailureSeverity severity = fma.GetSeverity();
                    string text = fma.GetDescriptionText() ?? "<no text>";

                    if (severity == FailureSeverity.Warning)
                    {
                        failuresAccessor.DeleteWarning(fma);
                        if (warningCounts.ContainsKey(text))
                            warningCounts[text]++;
                        else
                            warningCounts[text] = 1;
                    }
                    else
                    {
                        // Ошибки (не warnings) логируем каждую отдельно
                        _logger.Error("REVIT FAILURE [" + severity + "]: " + text);
                    }
                }

                foreach (var kvp in warningCounts)
                {
                    _logger.Warn("SUPPRESS WARNING x" + kvp.Value + ": " + kvp.Key);
                }

                return FailureProcessingResult.Continue;
            }
        }

        private FamilySymbol FindFamilySymbolByNames(Document doc, string familyName, string symbolName)
        {
            FilteredElementCollector collector = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol));

            foreach (FamilySymbol fs in collector)
            {
                if (string.Equals(fs.FamilyName, familyName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(fs.Name, symbolName, StringComparison.OrdinalIgnoreCase))
                {
                    return fs;
                }
            }

            return null;
        }

        private bool TrySetParameter(FamilyInstance inst, string paramName, double valueFt)
        {
            Parameter param = inst.LookupParameter(paramName);
            if (param == null || param.IsReadOnly) return false;
            if (param.StorageType == StorageType.Double)
            {
                param.Set(valueFt);
                return true;
            }
            return false;
        }

        private void LogInstanceParameters(FamilyInstance inst)
        {
            _logger.Info("=== Instance params (Id=" + inst.Id.IntegerValue + ") ===");
            foreach (Parameter p in inst.Parameters)
            {
                if (p == null) continue;
                string val;
                switch (p.StorageType)
                {
                    case StorageType.Double:
                        double ft = p.AsDouble();
                        val = ft.ToString("F4") + "ft (" + (ft * FEET_TO_MM).ToString("F1") + "mm)";
                        break;
                    case StorageType.Integer:
                        val = p.AsInteger().ToString();
                        break;
                    case StorageType.String:
                        val = "'" + (p.AsString() ?? "<null>") + "'";
                        break;
                    case StorageType.ElementId:
                        val = "ElemId=" + p.AsElementId().IntegerValue;
                        break;
                    default:
                        val = "?";
                        break;
                }
                _logger.Info("  '" + p.Definition.Name + "' [" + p.StorageType + "] = " + val
                    + (p.IsReadOnly ? " (RO)" : ""));
            }
        }

        private string FormatXyz(XYZ p)
        {
            if (p == null) return "<null>";
            return "(" + p.X.ToString("F3") + ", " + p.Y.ToString("F3") + ", " + p.Z.ToString("F3") + ")";
        }

        private string FormatFeetMm(double feet)
        {
            return feet.ToString("F4") + "ft (" + (feet * FEET_TO_MM).ToString("F0") + "mm)";
        }
    }

    internal static class XyzExtensions
    {
        internal static bool IsZeroLength(this XYZ v)
        {
            if (v == null) return true;
            return v.GetLength() < 1e-9;
        }
    }
}
