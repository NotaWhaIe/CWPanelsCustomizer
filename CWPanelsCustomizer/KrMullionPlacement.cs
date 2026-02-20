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
        private const double EDGE_OFFSET_MM = 150.0;
        private const double EDGE_OFFSET_FT = EDGE_OFFSET_MM / FEET_TO_MM;

        private const int BACKING_WALL_ID = 8091464;
        private const double MAX_FACADE_DIST_FT = 1000.0 / FEET_TO_MM;

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

            try
            {
                Method();
                _logger.EndSession("Succeeded");
            }
            catch (Exception ex)
            {
                _logger.Error("FAILED", ex);
                _logger.EndSession("Failed");
                throw;
            }

            return Result.Succeeded;
        }

        private void Method()
        {
            Stopwatch sw = Stopwatch.StartNew();
            _logger.Info("CODE_VERSION: atomic_txn_v1");

            // --- Опорная стена ---
            Wall backingWall = _doc.GetElement(new ElementId(BACKING_WALL_ID)) as Wall;
            if (backingWall == null)
            {
                _logger.Error("Backing wall not found. Id=" + BACKING_WALL_ID);
                throw new InvalidOperationException("Не найдена опорная стена Id=" + BACKING_WALL_ID);
            }
            _logger.Info("Backing wall: Id=" + BACKING_WALL_ID + " Name=" + backingWall.Name);

            List<PlanarFace> backingFaces = GetVerticalPlanarFaces(backingWall);
            _logger.Info("Backing wall vertical faces: " + backingFaces.Count);
            foreach (var face in backingFaces)
                _logger.Info("  Face: Normal=" + FormatXyz(face.FaceNormal) + " Origin=" + FormatXyz(face.Origin));

            if (backingFaces.Count == 0)
            {
                _logger.Error("No vertical planar faces on backing wall.");
                throw new InvalidOperationException("У опорной стены не найдено вертикальных граней.");
            }

            const string familyName = "КРСТ_НВФ_ZIAS_Стойка с кронштейнами в сборе_В2";
            const string symbolName = "Тип 1";
            const string oldFamilyName = "КРСТ_НВФ_ZIAS_Массив стоек с кронштейнами_В2";

            FamilySymbol symbol = FindFamilySymbolByNames(_doc, familyName, symbolName);
            if (symbol == null)
            {
                _logger.Error("FamilySymbol not found: '" + familyName + "' : '" + symbolName + "'");
                throw new InvalidOperationException("Не найдено семейство/тип: " + familyName + " : " + symbolName);
            }
            _logger.LogElement("FamilySymbol", symbol.Id.IntegerValue, symbol.FamilyName, symbol.Name);

            // --- Витражи с панелями КРСТ_НВФ_ ---
            List<Wall> allCurtainWalls = new FilteredElementCollector(_doc)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .Where(w => w != null && w.CurtainGrid != null)
                .ToList();

            _logger.Info("CurtainWalls total: " + allCurtainWalls.Count);

            List<Wall> nvfCurtainWalls = allCurtainWalls.Where(HasNvfPanels).ToList();
            _logger.Info("CurtainWalls with КРСТ_НВФ_ panels: " + nvfCurtainWalls.Count);

            if (nvfCurtainWalls.Count == 0)
            {
                _logger.Warn("No curtain walls with КРСТ_НВФ_ panels found.");
                return;
            }

            foreach (var cw in nvfCurtainWalls)
                _logger.Info("  CW Id=" + cw.Id.IntegerValue + " Name=" + cw.Name);

            // --- Сбор элементов для удаления (до транзакции) ---
            List<ElementId> toDeleteNew = new FilteredElementCollector(_doc)
                .WherePasses(new FamilyInstanceFilter(_doc, symbol.Id))
                .ToElementIds()
                .ToList();

            List<ElementId> toDeleteOld = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.Symbol != null &&
                    string.Equals(fi.Symbol.FamilyName, oldFamilyName, StringComparison.OrdinalIgnoreCase))
                .Select(fi => fi.Id)
                .ToList();

            List<ElementId> allToDelete = toDeleteNew.Concat(toDeleteOld).Distinct().ToList();
            _logger.Info("Queued for deletion: new=" + toDeleteNew.Count
                + " old=" + toDeleteOld.Count + " total=" + allToDelete.Count);

            int totalCreated = 0;
            int processedCount = 0;

            // --- Единая транзакция: сначала удаление, затем размещение ---
            using (Transaction t = new Transaction(_doc, IS_NAME))
            {
                t.Start();

                FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new DuplicateInstancesWarningSuppressor(_logger));
                t.SetFailureHandlingOptions(fho);

                // Шаг 1: удалить существующие стойки перед размещением
                if (allToDelete.Count > 0)
                {
                    _doc.Delete(allToDelete);
                    _logger.Info("Deleted: " + allToDelete.Count);
                }

                if (!symbol.IsActive)
                {
                    symbol.Activate();
                    _doc.Regenerate();
                }

                foreach (Wall curtainWall in nvfCurtainWalls)
                {
                    _logger.Info("=== CurtainWall Id=" + curtainWall.Id.IntegerValue + " Name=" + curtainWall.Name + " ===");

                    XYZ cwNormal = GetWallNormal(curtainWall);
                    if (cwNormal == null)
                    {
                        _logger.Warn("  Cannot determine wall normal, skip");
                        continue;
                    }
                    XYZ cwCenter = GetWallBBCenter(curtainWall);
                    _logger.Info("  Normal=" + FormatXyz(cwNormal) + " Center=" + FormatXyz(cwCenter));

                    PlanarFace matchingFace = FindClosestParallelFace(backingFaces, cwNormal, cwCenter);
                    if (matchingFace == null)
                    {
                        _logger.Warn("  No matching backing face, skip");
                        continue;
                    }

                    Plane workPlane = Plane.CreateByNormalAndOrigin(matchingFace.FaceNormal, matchingFace.Origin);
                    SketchPlane sketchPlane = SketchPlane.Create(_doc, workPlane);
                    _logger.Info("  WorkPlane: Normal=" + FormatXyz(workPlane.Normal) + " Origin=" + FormatXyz(workPlane.Origin));

                    int created = ProcessSingleCurtainWall(curtainWall, workPlane, sketchPlane, symbol);
                    totalCreated += created;
                    if (created > 0) processedCount++;
                    _logger.Info("  Subtotal created: " + created);
                }

                _logger.LogSummary("Placement result", ("Deleted", allToDelete.Count), ("Created", totalCreated), ("Facades", processedCount));
                t.Commit();
            }

            sw.Stop();
            _logger.Info("Execution time: " + sw.ElapsedMilliseconds + "ms");
        }

        private int ProcessSingleCurtainWall(Wall targetWall, Plane workPlane, SketchPlane sketchPlane, FamilySymbol symbol)
        {
            CurtainGrid curtainGrid = targetWall.CurtainGrid;
            if (curtainGrid == null) return 0;

            ICollection<ElementId> uIds = curtainGrid.GetUGridLineIds();
            ICollection<ElementId> vIds = curtainGrid.GetVGridLineIds();
            int uCount = uIds != null ? uIds.Count : 0;
            int vCount = vIds != null ? vIds.Count : 0;
            _logger.Info("  CurtainGrid lines: U=" + uCount + " V=" + vCount);

            List<CurtainGridLine> allGridLines = GetAllCurtainGridLines(curtainGrid);

            List<CurtainGridLine> verticalGridLines = allGridLines
                .Where(gl => gl != null)
                .Where(gl =>
                {
                    Curve c = GetGridLineCurve(gl);
                    if (c == null) return false;
                    XYZ dir = GetCurveDirection(c);
                    return Math.Abs(dir.DotProduct(XYZ.BasisZ)) >= 0.99;
                })
                .ToList();

            _logger.Info("  Vertical grid lines: " + verticalGridLines.Count);
            if (verticalGridLines.Count == 0) return 0;

            // --- Этап 1: Сбор данных линий сетки ---
            List<int> origIndices = new List<int>();
            List<CurtainGridLine> validGridLines = new List<CurtainGridLine>();
            List<XYZ> bottomPoints = new List<XYZ>();

            for (int i = 0; i < verticalGridLines.Count; i++)
            {
                Curve curve = GetGridLineCurve(verticalGridLines[i]);
                if (curve == null)
                {
                    _logger.Warn("  GridLine[" + i + "] Id=" + verticalGridLines[i].Id.IntegerValue + ": FullCurve is null -> skip");
                    continue;
                }
                origIndices.Add(i);
                validGridLines.Add(verticalGridLines[i]);
                bottomPoints.Add(GetCurveBottomPoint(curve));
            }

            // --- Этап 2: Сортировка и snap переходных Z ---
            XYZ wallHorizontal = workPlane.Normal.CrossProduct(XYZ.BasisZ);
            wallHorizontal = !wallHorizontal.IsZeroLength() ? wallHorizontal.Normalize() : XYZ.BasisX;

            int[] sortOrder = Enumerable.Range(0, bottomPoints.Count).ToArray();
            Array.Sort(sortOrder, (a, b) => wallHorizontal.DotProduct(bottomPoints[a])
                .CompareTo(wallHorizontal.DotProduct(bottomPoints[b])));

            double[] sortedZ = sortOrder.Select(idx => bottomPoints[idx].Z).ToArray();
            double[] snappedSortedZ = SnapTransitionBottomZ(sortedZ);

            double[] snappedZ = new double[bottomPoints.Count];
            for (int s = 0; s < sortOrder.Length; s++)
                snappedZ[sortOrder[s]] = snappedSortedZ[s];

            double[] actualTopZ = new double[validGridLines.Count];
            for (int i = 0; i < validGridLines.Count; i++)
            {
                Curve curve = GetGridLineCurve(validGridLines[i]);
                actualTopZ[i] = curve != null ? GetCurveTopPoint(curve).Z : 0;
            }

            // Проёмы (фильтр по фасаду) и рёбра контура
            const string openingFamilyName = "#_Оконный проем_Прямоугольный";
            List<double[]> openingDataList = CollectWindowOpenings(openingFamilyName, wallHorizontal, workPlane);
            const double OPENING_MATCH_TOLERANCE_FT = 50.0 / FEET_TO_MM;
            List<double[]> outlineEdges = GetWallOutlineVerticalEdges(targetWall, wallHorizontal);

            // --- Этап 3: Размещение стоек по линиям сетки ---
            int created = 0;

            for (int i = 0; i < validGridLines.Count; i++)
            {
                int origIdx = origIndices[i];
                XYZ bottomPt = bottomPoints[i];
                double bottomZ = snappedZ[i] + RACK_START_OFFSET_FT;
                double topZ = actualTopZ[i];

                double gridH_trim = wallHorizontal.DotProduct(bottomPt);
                TrimGridLineByOutlineEdges(outlineEdges, gridH_trim, ref bottomZ, ref topZ);

                bool wasSnapped = Math.Abs(snappedZ[i] - bottomPt.Z) > 0.001;
                string snapInfo = wasSnapped ? " snap=" + FormatFeetMm(snappedZ[i]) : "";

                if (topZ - bottomZ < 0.01)
                {
                    _logger.Warn("  GridLine[" + origIdx + "]: totalHeight too small, skip");
                    continue;
                }

                double gridLineHPos = wallHorizontal.DotProduct(bottomPt);

                List<double[]> matchingOpenings = FindOpeningsOverlappingGridLine(
                    openingDataList, gridLineHPos);
                List<double[]> freeSegments = GetFreeSegments(bottomZ, topZ, matchingOpenings);

                _logger.Info("  GridLine[" + origIdx + "] Id=" + validGridLines[i].Id.IntegerValue
                    + snapInfo
                    + " bottomZ=" + FormatFeetMm(bottomZ)
                    + " topZ=" + FormatFeetMm(topZ)
                    + " openings=" + matchingOpenings.Count
                    + " segments=" + freeSegments.Count);

                if (matchingOpenings.Count > 0)
                    foreach (var seg in freeSegments)
                        _logger.Info("      FreeSegment: " + FormatFeetMm(seg[0]) + ".." + FormatFeetMm(seg[1])
                            + " h=" + FormatFeetMm(seg[1] - seg[0]));

                created += PlaceRacksInSegments(freeSegments, bottomPt, workPlane, sketchPlane, symbol, "piece");

                // --- Боковые стойки у окон ---
                if (matchingOpenings.Count > 0)
                {
                    var openingGroups = new Dictionary<int, List<double[]>>();
                    foreach (var op in matchingOpenings)
                    {
                        int key = (int)Math.Round(op[0] * FEET_TO_MM);
                        if (!openingGroups.ContainsKey(key))
                            openingGroups[key] = new List<double[]>();
                        openingGroups[key].Add(op);
                    }

                    foreach (var grp in openingGroups)
                    {
                        double oCenter = grp.Value[0][0];

                        if (Math.Abs(oCenter - gridLineHPos) <= OPENING_MATCH_TOLERANCE_FT)
                            continue;

                        bool gridIsRight = gridLineHPos > oCenter;
                        double nearEdge = gridIsRight ? grp.Value[0][4] : grp.Value[0][3];
                        double sideH = gridIsRight
                            ? nearEdge + EDGE_OFFSET_FT
                            : nearEdge - EDGE_OFFSET_FT;

                        double refH = wallHorizontal.DotProduct(bottomPt);
                        XYZ sideBasePt = new XYZ(
                            bottomPt.X + wallHorizontal.X * (sideH - refH),
                            bottomPt.Y + wallHorizontal.Y * (sideH - refH),
                            0);

                        _logger.Info("    SideRacks: gridH=" + FormatFeetMm(gridLineHPos)
                            + " windowCenter=" + FormatFeetMm(oCenter)
                            + " sideH=" + FormatFeetMm(sideH)
                            + " openings=" + grp.Value.Count);

                        var sideSegments = new List<double[]>();
                        foreach (var op in grp.Value)
                        {
                            double sideZBot = op[1];
                            double sideZTop = op[2] - OPENING_TOP_OFFSET_FT;
                            if (sideZTop - sideZBot < RACK_MIN_HEIGHT_FT * 0.5) continue;
                            sideSegments.Add(new[] { sideZBot, sideZTop });
                        }

                        if (sideSegments.Count > 0)
                            created += PlaceRacksInSegments(sideSegments, sideBasePt,
                                workPlane, sketchPlane, symbol, "side_piece");
                    }
                }
            }

            // --- Этап 4: Размещение стоек по краям витража ---
            _logger.Info("  === Edge mullion placement ===");
            List<double[]> mergedEdges = ExtendOutlineEdgesZ(outlineEdges);
            _logger.Info("  Wall outline vertical edges: " + outlineEdges.Count + " (after extend: " + mergedEdges.Count + ")");

            foreach (var edge in mergedEdges)
            {
                double edgeH = edge[0];
                double edgeZBot = edge[1];
                double edgeZTop = edge[2];

                _logger.Info("  OutlineEdge: H=" + FormatFeetMm(edgeH)
                    + " Z=" + FormatFeetMm(edgeZBot) + ".." + FormatFeetMm(edgeZTop));

                double mullionH = ComputeEdgeMullionHPos(
                    edgeH, edgeZBot, edgeZTop,
                    bottomPoints, actualTopZ, snappedZ, wallHorizontal);

                bool tooCloseToGrid = false;
                for (int i = 0; i < bottomPoints.Count; i++)
                {
                    double gridH = wallHorizontal.DotProduct(bottomPoints[i]);
                    if (Math.Abs(gridH - mullionH) < EDGE_OFFSET_FT * 0.5
                        && actualTopZ[i] > edgeZBot + 0.01
                        && snappedZ[i] < edgeZTop - 0.01)
                    {
                        tooCloseToGrid = true;
                        break;
                    }
                }
                if (tooCloseToGrid)
                {
                    _logger.Info("    Skip: too close to existing grid line");
                    continue;
                }

                double edgeBottomZ = edgeZBot + RACK_START_OFFSET_FT;
                double edgeTopZ = edgeZTop;

                if (edgeTopZ - edgeBottomZ < RACK_MIN_HEIGHT_FT - 0.001)
                {
                    _logger.Info("    Skip: height too small (" + FormatFeetMm(edgeTopZ - edgeBottomZ) + ")");
                    continue;
                }

                XYZ refPt = bottomPoints.Count > 0 ? bottomPoints[0] : workPlane.Origin;
                double refH2 = wallHorizontal.DotProduct(refPt);
                XYZ edgeBasePt = new XYZ(
                    refPt.X + wallHorizontal.X * (mullionH - refH2),
                    refPt.Y + wallHorizontal.Y * (mullionH - refH2),
                    0);

                List<double[]> edgeOpenings = FindOpeningsForGridLine(
                    openingDataList, mullionH, OPENING_MATCH_TOLERANCE_FT);
                List<double[]> edgeSegments = GetFreeSegments(edgeBottomZ, edgeTopZ, edgeOpenings);

                _logger.Info("    EdgeMullion: H=" + FormatFeetMm(mullionH)
                    + " Z=" + FormatFeetMm(edgeBottomZ) + ".." + FormatFeetMm(edgeTopZ)
                    + " openings=" + edgeOpenings.Count
                    + " segments=" + edgeSegments.Count);

                created += PlaceRacksInSegments(edgeSegments, edgeBasePt, workPlane, sketchPlane, symbol, "edge_piece");
            }

            return created;
        }

        /// <summary>
        /// Размещает стойки с разбивкой по высоте в заданных свободных сегментах.
        /// Возвращает количество созданных экземпляров.
        /// </summary>
        private int PlaceRacksInSegments(List<double[]> segments, XYZ basePt,
            Plane workPlane, SketchPlane sketchPlane, FamilySymbol symbol, string logPrefix)
        {
            int created = 0;
            int pieceIdx = 0;

            foreach (var segment in segments)
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

                    XYZ placementPt = new XYZ(basePt.X, basePt.Y, currentZ);
                    XYZ projected = ProjectPointToPlane(placementPt, workPlane);

                    FamilyInstance inst = _doc.Create.NewFamilyInstance(
                        projected, symbol, sketchPlane, StructuralType.NonStructural);
                    bool paramSet = TrySetParameter(inst, "Профиль_Длина", pieceHeightFt);

                    _logger.Info("    " + logPrefix + "[" + pieceIdx + "] Id=" + inst.Id.IntegerValue
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

            return created;
        }

        #region Helpers

        private List<PlanarFace> GetVerticalPlanarFaces(Wall wall)
        {
            var faces = new List<PlanarFace>();
            Options opts = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
            GeometryElement geoElem = wall.get_Geometry(opts);
            if (geoElem == null) return faces;

            foreach (GeometryObject geoObj in geoElem)
            {
                Solid solid = geoObj as Solid;
                if (solid == null || solid.Faces.Size == 0) continue;
                foreach (Face f in solid.Faces)
                {
                    PlanarFace pf = f as PlanarFace;
                    if (pf == null) continue;
                    if (Math.Abs(pf.FaceNormal.DotProduct(XYZ.BasisZ)) < 0.1)
                        faces.Add(pf);
                }
            }
            return faces;
        }

        private XYZ GetWallNormal(Wall wall)
        {
            LocationCurve locCurve = wall.Location as LocationCurve;
            if (locCurve == null) return null;
            Curve curve = locCurve.Curve;
            XYZ start = curve.GetEndPoint(0);
            XYZ end = curve.GetEndPoint(1);
            XYZ direction = end - start;
            if (direction.IsZeroLength()) return null;
            XYZ normal = direction.Normalize().CrossProduct(XYZ.BasisZ).Normalize();
            return normal;
        }

        private XYZ GetWallBBCenter(Wall wall)
        {
            BoundingBoxXYZ bb = wall.get_BoundingBox(null);
            if (bb == null) return XYZ.Zero;
            return (bb.Min + bb.Max) * 0.5;
        }

        private PlanarFace FindClosestParallelFace(List<PlanarFace> faces, XYZ normal, XYZ point)
        {
            PlanarFace best = null;
            double bestDist = double.MaxValue;

            foreach (PlanarFace face in faces)
            {
                double dot = Math.Abs(face.FaceNormal.DotProduct(normal));
                if (dot < 0.95) continue;

                double dist = Math.Abs(face.FaceNormal.DotProduct(point - face.Origin));
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = face;
                }
            }

            if (best != null)
                _logger.Info("  Matched face: dist=" + FormatFeetMm(bestDist) + " Normal=" + FormatXyz(best.FaceNormal));

            return best;
        }

        private bool HasNvfPanels(Wall curtainWall)
        {
            CurtainGrid grid = curtainWall.CurtainGrid;
            if (grid == null) return false;

            ICollection<ElementId> panelIds = grid.GetPanelIds();
            if (panelIds == null || panelIds.Count == 0) return false;

            foreach (ElementId panelId in panelIds)
            {
                FamilyInstance fi = _doc.GetElement(panelId) as FamilyInstance;
                if (fi != null && fi.Symbol != null &&
                    fi.Symbol.FamilyName.StartsWith("КРСТ_НВФ_", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private List<CurtainGridLine> GetAllCurtainGridLines(CurtainGrid grid)
        {
            var result = new List<CurtainGridLine>();
            if (grid == null) return result;

            foreach (ElementId id in grid.GetUGridLineIds() ?? new List<ElementId>())
            {
                CurtainGridLine gl = _doc.GetElement(id) as CurtainGridLine;
                if (gl != null) result.Add(gl);
            }
            foreach (ElementId id in grid.GetVGridLineIds() ?? new List<ElementId>())
            {
                CurtainGridLine gl = _doc.GetElement(id) as CurtainGridLine;
                if (gl != null) result.Add(gl);
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
            XYZ p0 = curve.GetEndPoint(0), p1 = curve.GetEndPoint(1);
            return p0.Z <= p1.Z ? p0 : p1;
        }

        private XYZ GetCurveTopPoint(Curve curve)
        {
            XYZ p0 = curve.GetEndPoint(0), p1 = curve.GetEndPoint(1);
            return p0.Z >= p1.Z ? p0 : p1;
        }

        private XYZ GetCurveDirection(Curve curve)
        {
            if (curve == null) return XYZ.BasisX;
            XYZ v = curve.GetEndPoint(1) - curve.GetEndPoint(0);
            return v.IsZeroLength() ? XYZ.BasisX : v.Normalize();
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

        /// <summary>
        /// Собирает оконные проёмы с фильтрацией по расстоянию до фасадной плоскости.
        /// Возвращает [hCenter, zMin, zMax, hLeft, hRight].
        /// </summary>
        private List<double[]> CollectWindowOpenings(string familyName, XYZ wallHorizontal, Plane facadePlane)
        {
            var result = new List<double[]>();

            List<FamilyInstance> openings = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .Where(fi => fi.Symbol != null &&
                    string.Equals(fi.Symbol.FamilyName, familyName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            _logger.Info("  Window openings ('" + familyName + "'): " + openings.Count + " total");

            foreach (var opening in openings)
            {
                BoundingBoxXYZ bb = opening.get_BoundingBox(null);
                if (bb == null) continue;
                LocationPoint locPt = opening.Location as LocationPoint;
                if (locPt == null) continue;

                // Фильтр по расстоянию до фасадной плоскости
                if (facadePlane != null)
                {
                    double normalDist = Math.Abs(facadePlane.Normal.DotProduct(locPt.Point - facadePlane.Origin));
                    if (normalDist > MAX_FACADE_DIST_FT) continue;
                }

                double hPos = wallHorizontal.DotProduct(locPt.Point);

                double h1 = wallHorizontal.X * bb.Min.X + wallHorizontal.Y * bb.Min.Y;
                double h2 = wallHorizontal.X * bb.Min.X + wallHorizontal.Y * bb.Max.Y;
                double h3 = wallHorizontal.X * bb.Max.X + wallHorizontal.Y * bb.Min.Y;
                double h4 = wallHorizontal.X * bb.Max.X + wallHorizontal.Y * bb.Max.Y;
                double hLeft = Math.Min(Math.Min(h1, h2), Math.Min(h3, h4));
                double hRight = Math.Max(Math.Max(h1, h2), Math.Max(h3, h4));

                result.Add(new[] { hPos, bb.Min.Z, bb.Max.Z, hLeft, hRight });

                _logger.Info("    Opening Id=" + opening.Id.IntegerValue
                    + " hPos=" + FormatFeetMm(hPos)
                    + " Z=" + FormatFeetMm(bb.Min.Z) + ".." + FormatFeetMm(bb.Max.Z)
                    + " hRange=" + FormatFeetMm(hLeft) + ".." + FormatFeetMm(hRight));
            }

            _logger.Info("  Openings for facade: " + result.Count);
            return result;
        }

        private List<double[]> FindOpeningsForGridLine(
            List<double[]> allOpenings, double gridLineHPos, double tolerance)
        {
            var result = new List<double[]>();
            foreach (var opening in allOpenings)
                if (Math.Abs(opening[0] - gridLineHPos) <= tolerance)
                    result.Add(opening);
            result.Sort((a, b) => a[1].CompareTo(b[1]));
            return result;
        }

        /// <summary>
        /// Находит проёмы, чей горизонтальный BoundingBox охватывает позицию линии сетки.
        /// Использует [3]=hLeft, [4]=hRight из CollectWindowOpenings.
        /// </summary>
        private List<double[]> FindOpeningsOverlappingGridLine(
            List<double[]> allOpenings, double gridLineHPos)
        {
            var result = new List<double[]>();
            foreach (var opening in allOpenings)
            {
                double hLeft = opening[3];
                double hRight = opening[4];
                if (gridLineHPos >= hLeft - 0.001 && gridLineHPos <= hRight + 0.001)
                    result.Add(opening);
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
                        segments.Add(new[] { currentStart, segEnd });
                }

                currentStart = Math.Max(currentStart, openingMax - OPENING_TOP_OFFSET_FT);
                if (currentStart >= topZ - 0.001) break;
            }

            if (currentStart < topZ - 0.001)
                segments.Add(new[] { currentStart, topZ });

            return segments;
        }

        /// <summary>
        /// Извлекает вертикальные рёбра контура витражной стены.
        /// Для стен с профилем — из Sketch.Profile, иначе — из LocationCurve + высота.
        /// Возвращает список [hPosition, zBottom, zTop].
        /// </summary>
        private List<double[]> GetWallOutlineVerticalEdges(Wall wall, XYZ wallHorizontal)
        {
            var edges = new List<double[]>();

            ICollection<ElementId> depIds = wall.GetDependentElements(
                new ElementClassFilter(typeof(Sketch)));

            List<Curve> outlineCurves = new List<Curve>();

            if (depIds != null && depIds.Count > 0)
            {
                foreach (ElementId id in depIds)
                {
                    Sketch sketch = _doc.GetElement(id) as Sketch;
                    if (sketch == null || sketch.Profile == null) continue;
                    foreach (CurveArray loop in sketch.Profile)
                        foreach (Curve c in loop)
                            outlineCurves.Add(c);
                    if (outlineCurves.Count > 0) break;
                }
            }

            _logger.Info("  Wall outline curves from Sketch: " + outlineCurves.Count
                + " (Sketches found: " + (depIds != null ? depIds.Count : 0) + ")");

            if (outlineCurves.Count == 0)
            {
                LocationCurve locCurve = wall.Location as LocationCurve;
                if (locCurve != null)
                {
                    Curve baseline = locCurve.Curve;
                    Parameter heightParam = wall.get_Parameter(BuiltInParameter.WALL_USER_HEIGHT_PARAM);
                    Parameter baseOffsetParam = wall.get_Parameter(BuiltInParameter.WALL_BASE_OFFSET);
                    double height = heightParam != null ? heightParam.AsDouble() : 0;
                    double baseOffset = baseOffsetParam != null ? baseOffsetParam.AsDouble() : 0;

                    XYZ p0 = baseline.GetEndPoint(0), p1 = baseline.GetEndPoint(1);
                    double h0 = wallHorizontal.DotProduct(p0), h1 = wallHorizontal.DotProduct(p1);
                    double zBot = Math.Min(p0.Z, p1.Z) + baseOffset;
                    double zTop = zBot + height;

                    edges.Add(new[] { Math.Min(h0, h1), zBot, zTop });
                    edges.Add(new[] { Math.Max(h0, h1), zBot, zTop });

                    _logger.Info("  Fallback rectangular outline: left=" + FormatFeetMm(Math.Min(h0, h1))
                        + " right=" + FormatFeetMm(Math.Max(h0, h1))
                        + " Z=" + FormatFeetMm(zBot) + ".." + FormatFeetMm(zTop));
                }
                return edges;
            }

            foreach (Curve c in outlineCurves)
            {
                if (c == null) continue;
                XYZ p0 = c.GetEndPoint(0), p1 = c.GetEndPoint(1);
                if (p0 == null || p1 == null) continue;

                XYZ diff = p1 - p0;
                if (diff.IsZeroLength()) continue;
                if (Math.Abs(diff.Normalize().DotProduct(XYZ.BasisZ)) < 0.99) continue;

                double hPos = wallHorizontal.DotProduct((p0 + p1) * 0.5);
                double zBot = Math.Min(p0.Z, p1.Z);
                double zTop = Math.Max(p0.Z, p1.Z);
                if (zTop - zBot < 0.01) continue;

                edges.Add(new[] { hPos, zBot, zTop });
                _logger.Info("    OutlineCurve: vertical H=" + FormatFeetMm(hPos)
                    + " Z=" + FormatFeetMm(zBot) + ".." + FormatFeetMm(zTop));
            }

            _logger.Info("  Vertical outline edges found: " + edges.Count);
            return edges;
        }

        private double ComputeEdgeMullionHPos(
            double edgeH, double edgeZBot, double edgeZTop,
            List<XYZ> gridLineBottomPts, double[] gridLineTopZ, double[] snappedGridZ,
            XYZ wallHorizontal)
        {
            double nearestLeftDist = double.MaxValue;
            double nearestRightDist = double.MaxValue;

            for (int i = 0; i < gridLineBottomPts.Count; i++)
            {
                double gridH = wallHorizontal.DotProduct(gridLineBottomPts[i]);
                if (gridLineTopZ[i] < edgeZBot + 0.01 || snappedGridZ[i] > edgeZTop - 0.01) continue;

                double dist = gridH - edgeH;
                if (dist > 0.001 && dist < nearestRightDist) nearestRightDist = dist;
                else if (dist < -0.001 && Math.Abs(dist) < nearestLeftDist) nearestLeftDist = Math.Abs(dist);
            }

            return nearestRightDist <= nearestLeftDist ? edgeH + EDGE_OFFSET_FT : edgeH - EDGE_OFFSET_FT;
        }

        private void TrimGridLineByOutlineEdges(List<double[]> outlineEdges, double gridH,
            ref double bottomZ, ref double topZ)
        {
            const double H_TOLERANCE_FT = 5.0 / FEET_TO_MM;

            foreach (var edge in outlineEdges)
            {
                if (Math.Abs(edge[0] - gridH) > H_TOLERANCE_FT) continue;

                double edgeZBot = edge[1], edgeZTop = edge[2];

                if (edgeZBot < bottomZ + 0.01 && edgeZTop > bottomZ + 0.01 && edgeZTop < topZ - 0.01)
                    bottomZ = edgeZTop + RACK_START_OFFSET_FT;

                if (edgeZBot > bottomZ + 0.01 && edgeZBot < topZ - 0.01 && edgeZTop > topZ - 0.01)
                    topZ = edgeZBot;
            }
        }

        private List<double[]> ExtendOutlineEdgesZ(List<double[]> rawEdges)
        {
            const double Z_TOL_FT = 1.0 / FEET_TO_MM;
            const double H_MERGE_TOL_FT = 1000.0 / FEET_TO_MM;

            var result = new List<double[]>();

            for (int i = 0; i < rawEdges.Count; i++)
            {
                double h = rawEdges[i][0], zBot = rawEdges[i][1], zTop = rawEdges[i][2];

                bool found;
                do
                {
                    found = false;
                    for (int j = 0; j < rawEdges.Count; j++)
                    {
                        if (j == i) continue;
                        if (Math.Abs(rawEdges[j][0] - h) > H_MERGE_TOL_FT) continue;
                        if (Math.Abs(rawEdges[j][1] - zTop) < Z_TOL_FT && rawEdges[j][2] > zTop + 0.001)
                        {
                            zTop = rawEdges[j][2];
                            found = true;
                            break;
                        }
                    }
                } while (found);

                result.Add(new[] { h, zBot, zTop });
            }

            return result;
        }

        private XYZ ProjectPointToPlane(XYZ point, Plane plane)
        {
            if (point == null || plane == null) return point;
            double d = plane.Normal.DotProduct(point - plane.Origin);
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
            { t.Start(); doc.Delete(toDelete); t.Commit(); }

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
            { t.Start(); doc.Delete(toDelete); t.Commit(); }

            return toDelete.Count;
        }

        private FamilySymbol FindFamilySymbolByNames(Document doc, string familyName, string symbolName)
        {
            foreach (FamilySymbol fs in new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)))
                if (string.Equals(fs.FamilyName, familyName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(fs.Name, symbolName, StringComparison.OrdinalIgnoreCase))
                    return fs;
            return null;
        }

        private bool TrySetParameter(FamilyInstance inst, string paramName, double valueFt)
        {
            Parameter param = inst.LookupParameter(paramName);
            if (param == null || param.IsReadOnly || param.StorageType != StorageType.Double) return false;
            param.Set(valueFt);
            return true;
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

        #endregion

        private class DuplicateInstancesWarningSuppressor : IFailuresPreprocessor
        {
            private readonly RevitLogger _logger;

            public DuplicateInstancesWarningSuppressor(RevitLogger logger) { _logger = logger; }

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
                        if (warningCounts.ContainsKey(text)) warningCounts[text]++;
                        else warningCounts[text] = 1;
                    }
                    else
                    {
                        _logger.Error("REVIT FAILURE [" + severity + "]: " + text);
                    }
                }

                foreach (var kvp in warningCounts)
                    _logger.Warn("SUPPRESS WARNING x" + kvp.Value + ": " + kvp.Key);

                return FailureProcessingResult.Continue;
            }
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
