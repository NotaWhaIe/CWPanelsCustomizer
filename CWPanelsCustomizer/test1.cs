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

        private const double EPS = 1e-9;
        private const double FEET_TO_MM = 304.8;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            _uidoc = commandData.Application.ActiveUIDocument;
            _doc = _uidoc.Document;

            Debug.WriteLine("[CWPanelsCustomizer] Execute START");

            using (TransactionGroup tg = new TransactionGroup(_doc, IS_NAME))
            {
                tg.Start();

                // 0) Сбор данных
                List<CurtainWallDataDto> data = GetElements(_doc);

                // 1) Сброс подрезок рядовых панелей по пересечению с проёмами
                ResetRegularPanelsCutsForIntersectingOpenings(data);

                // 2) Замена рядовых панелей на угловые (где нужно)
                ReplaceRegularPanelsWithCutoutPanels(data);

                // 3) Подрезки рядовых панелей
                CalculateAndSetRegularPanelsCuts(data);

                // 4) Настройка угловых панелей по значениям рядовых
                CalculateAndSetCutoutPanelsCuts(data);

                // 5) НОВОЕ: отзеркаливание панелей справа от окна, пересекающихся с BB окна
                //MirrorPanelsRightOfOpenings(data);

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

        // ==========================================================
        // === NEW FEATURE: MIRROR PANELS RIGHT OF OPENING (BY BB) ===
        // ==========================================================
        private void MirrorPanelsRightOfOpenings(List<CurtainWallDataDto> data)
        {
            const string TAG = "[MirrorPanelsRightOfOpenings]";

            Debug.WriteLine($"{TAG} START");

            if (data == null || data.Count == 0)
            {
                Debug.WriteLine($"{TAG} data is null/empty -> END");
                return;
            }

            int panelsTotal = 0;
            int candidates = 0;
            int mirroredNow = 0;
            int skippedAlreadyMirrored = 0;
            int flipErrors = 0;

            using (var t = new Transaction(_doc, "CW: Mirror panels right of opening (BB)"))
            {
                t.Start();

                // чтобы BoundingBox был актуальный после предыдущих операций
                _doc.Regenerate();

                foreach (var cw in data)
                {
                    if (cw?.CurtainWallElement == null)
                        continue;

                    var openings = cw.IntersectingOpenings ?? new List<OpeningModelDto>();
                    var panels = cw.Panels ?? new List<CurtainWallPanelDto>();

                    if (openings.Count == 0 || panels.Count == 0)
                        continue;

                    foreach (var opening in openings)
                    {
                        if (opening?.OpeningElement == null)
                            continue;

                        var obWorld = GetWorldBBoxFresh(opening.OpeningElement);
                        if (obWorld == null)
                            continue;

                        var wCenterX = CenterOf(obWorld).X;

                        foreach (var pdto in panels)
                        {
                            if (pdto?.PanelElement == null)
                                continue;

                            panelsTotal++;

                            var pbWorld = GetWorldBBoxFresh(pdto.PanelElement);
                            if (pbWorld == null)
                                continue;

                            // пересечение BB (XYZ)
                            if (!BoundingBoxesIntersect(pbWorld, obWorld))
                                continue;

                            // справа от окна (по X проекта)
                            var pCenterX = CenterOf(pbWorld).X;
                            if (!(pCenterX > wCenterX))
                                continue;

                            // кандидат
                            candidates++;

                            // DTO - источник истины
                            pdto.IsMirrored = true;

                            // идемпотентность: если уже mirrored - не трогаем
                            bool alreadyMirrored = false;
                            try
                            {
                                alreadyMirrored = pdto.PanelElement.Mirrored;
                            }
                            catch
                            {
                                // если вдруг API не даёт Mirrored (маловероятно), считаем что нет
                                alreadyMirrored = false;
                            }

                            if (alreadyMirrored)
                            {
                                skippedAlreadyMirrored++;
                                continue;
                            }

                            // пытаемся "как пробел": flipHand / flipFacing (что доступно)
                            try
                            {
                                bool flipped = false;

                                // Hand flip обычно ближе всего к "пробелу" для многих семейств
                                if (pdto.PanelElement.CanFlipHand)
                                {
                                    pdto.PanelElement.flipHand();
                                    flipped = true;
                                }
                                else if (pdto.PanelElement.CanFlipFacing)
                                {
                                    pdto.PanelElement.flipFacing();
                                    flipped = true;
                                }

                                if (!flipped)
                                {
                                    // ничего не делаем, но это не ошибка процесса — только лог
                                    flipErrors++;
                                    Debug.WriteLine($"{TAG} panelId={pdto.PanelElement.Id.IntegerValue} cannot flip (CanFlipHand/CanFlipFacing=false)");
                                    continue;
                                }

                                // повторно проверим фактическое состояние
                                bool nowMirrored = false;
                                try { nowMirrored = pdto.PanelElement.Mirrored; } catch { nowMirrored = false; }

                                if (nowMirrored)
                                {
                                    mirroredNow++;
                                }
                                else
                                {
                                    // если flip прошёл, но Mirrored не стал true — считаем как ошибка по ТЗ
                                    flipErrors++;
                                    Debug.WriteLine($"{TAG} panelId={pdto.PanelElement.Id.IntegerValue} flip done but Mirrored=false");
                                }
                            }
                            catch (Exception ex)
                            {
                                flipErrors++;
                                Debug.WriteLine($"{TAG} panelId={pdto.PanelElement.Id.IntegerValue} flip ERROR: {ex.Message}");
                            }
                        }
                    }
                }

                _doc.Regenerate();
                t.Commit();
            }

            Debug.WriteLine($"{TAG} panelsTotal={panelsTotal}");
            Debug.WriteLine($"{TAG} candidates={candidates}");
            Debug.WriteLine($"{TAG} mirroredNow={mirroredNow}");
            Debug.WriteLine($"{TAG} skippedAlreadyMirrored={skippedAlreadyMirrored}");
            Debug.WriteLine($"{TAG} flipErrors={flipErrors}");
            Debug.WriteLine($"{TAG} END");
        }

        // ===========================
        // === CUTOUT FEATURE (NEW) ===
        // ===========================
        private void CalculateAndSetCutoutPanelsCuts(List<CurtainWallDataDto> data)
        {
            const string TAG = "[CalculateAndSetCutoutPanelsCuts]";
            const string REGULAR_FAMILY = "КРСТ_НВФ_Рядовая_В3";
            const string CUTOUT_TOP_FAMILY = "КРСТ_НВФ_С Г-образным вырезом_В2";
            const string CUTOUT_BOTTOM_FAMILY = "КРСТ_НВФ_С L-образным вырезом";

            const string REG_PARAM_HOR = "Подрезка";
            const string REG_PARAM_TOP_HEIGHT = "Подрезка_Низ";
            const string REG_PARAM_BOTTOM_HEIGHT = "Подрезка_Верх";

            const string CUT_PARAM_W = "Вырез_Ширина";
            const string CUT_PARAM_H = "Вырез_Высота";

            const double CHECK_SEGMENT_LENGTH_FT = 0.328084; // 100 мм
            const double PANEL_BBOX_REDUCTION_FACTOR = 0.70;

            Debug.WriteLine($"{TAG} START");

            if (data == null || data.Count == 0)
            {
                Debug.WriteLine($"{TAG} data is null/empty -> END");
                return;
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

            int wallsProcessed = 0;
            int openingsProcessed = 0;
            int cutoutPanelsFound = 0;
            int cutoutPanelsUpdated = 0;
            int paramsSet = 0;

            using (var t = new Transaction(_doc, "CW: Set cutout panel cuts by regular panel values"))
            {
                t.Start();
                _doc.Regenerate();

                foreach (var cw in data)
                {
                    if (cw?.CurtainWallElement == null)
                        continue;

                    wallsProcessed++;

                    int wallId = cw.CurtainWallElement.Id.IntegerValue;
                    var openings = cw.IntersectingOpenings ?? new List<OpeningModelDto>();
                    var panelsAll = cw.Panels ?? new List<CurtainWallPanelDto>();

                    if (openings.Count == 0 || panelsAll.Count == 0)
                        continue;

                    var regularPanels = panelsAll
                        .Where(p => p?.PanelElement != null)
                        .Where(p => p.PanelElement.Symbol?.Family?.Name == REGULAR_FAMILY)
                        .ToList();

                    var cutoutPanels = panelsAll
                        .Where(p => p?.PanelElement != null)
                        .Where(p =>
                        {
                            var fam = p.PanelElement.Symbol?.Family?.Name ?? "";
                            return fam == CUTOUT_TOP_FAMILY || fam == CUTOUT_BOTTOM_FAMILY;
                        })
                        .ToList();

                    Debug.WriteLine($"{TAG} wallId={wallId}, openings={openings.Count}, regularPanels={regularPanels.Count}, cutoutPanels={cutoutPanels.Count}");

                    if (regularPanels.Count == 0 || cutoutPanels.Count == 0)
                        continue;

                    foreach (var op in openings)
                    {
                        if (op?.OpeningElement == null)
                            continue;

                        var opBox = GetLocalBBoxFresh(op.OpeningElement, cw.InverseTransform);
                        if (opBox == null)
                        {
                            Debug.WriteLine($"{TAG} wallId={wallId}: opening bbox null -> skip");
                            continue;
                        }

                        openingsProcessed++;
                        int opId = op.OpeningElement.Id.IntegerValue;
                        var opC = CenterOf(opBox);

                        FamilyInstance bestLeft = null, bestRight = null, bestTop = null, bestBottom = null;
                        double bestLeftScore = 0, bestRightScore = 0, bestTopScore = 0, bestBottomScore = 0;

                        foreach (var rp in regularPanels)
                        {
                            var fi = rp.PanelElement;
                            if (fi == null) continue;

                            var pBox = GetLocalBBoxFresh(fi, cw.InverseTransform);
                            if (pBox == null) continue;

                            if (!Intersects3D(opBox, pBox))
                                continue;

                            var pC = CenterOf(pBox);
                            double dx = pC.X - opC.X;
                            double dz = pC.Z - opC.Z;

                            if (Math.Abs(dz) >= Math.Abs(dx))
                            {
                                double score = OverlapZ(opBox, pBox);
                                if (score <= EPS) continue;

                                if (dz > 0)
                                {
                                    if (score > bestTopScore) { bestTopScore = score; bestTop = fi; }
                                }
                                else
                                {
                                    if (score > bestBottomScore) { bestBottomScore = score; bestBottom = fi; }
                                }
                            }
                            else
                            {
                                double score = OverlapX(opBox, pBox);
                                if (score <= EPS) continue;

                                if (dx < 0)
                                {
                                    if (score > bestLeftScore) { bestLeftScore = score; bestLeft = fi; }
                                }
                                else
                                {
                                    if (score > bestRightScore) { bestRightScore = score; bestRight = fi; }
                                }
                            }
                        }

                        var candidateCutouts = new List<(FamilyInstance fi, BoundingBoxXYZ bbox)>();
                        foreach (var cp in cutoutPanels)
                        {
                            var fi = cp.PanelElement;
                            if (fi == null) continue;

                            var pBox = GetLocalBBoxFresh(fi, cw.InverseTransform);
                            if (pBox == null) continue;

                            var reduced = Reduce(pBox, PANEL_BBOX_REDUCTION_FACTOR);
                            if (BBoxIntersect(opBox, reduced))
                                candidateCutouts.Add((fi, reduced));
                        }

                        if (candidateCutouts.Count == 0)
                            continue;

                        var windowCornerTL = new XYZ(opBox.Min.X, 0, opBox.Max.Z);
                        var windowCornerTR = new XYZ(opBox.Max.X, 0, opBox.Max.Z);
                        var windowCornerBL = new XYZ(opBox.Min.X, 0, opBox.Min.Z);
                        var windowCornerBR = new XYZ(opBox.Max.X, 0, opBox.Min.Z);

                        var corners = new List<(XYZ corner, XYZ dirV, XYZ dirH, string name)>
                        {
                            (windowCornerTL, new XYZ(0,0, 1), new XYZ(-1,0,0), "TL"),
                            (windowCornerTR, new XYZ(0,0, 1), new XYZ( 1,0,0), "TR"),
                            (windowCornerBL, new XYZ(0,0,-1), new XYZ(-1,0,0), "BL"),
                            (windowCornerBR, new XYZ(0,0,-1), new XYZ( 1,0,0), "BR"),
                        };

                        var foundCorners = new Dictionary<string, FamilyInstance>();

                        foreach (var c in corners)
                        {
                            var p1v = c.corner;
                            var p2v = c.corner + c.dirV * CHECK_SEGMENT_LENGTH_FT;

                            var p1h = c.corner;
                            var p2h = c.corner + c.dirH * CHECK_SEGMENT_LENGTH_FT;

                            var hitV = GetHitPanelsBySegment2D(candidateCutouts, p1v, p2v);
                            var hitH = GetHitPanelsBySegment2D(candidateCutouts, p1h, p2h);

                            var common = hitV.Intersect(hitH).ToList();
                            if (common.Count == 0)
                                continue;

                            FamilyInstance best = null;
                            double bestDist = double.MaxValue;

                            foreach (var fi in common)
                            {
                                var bb = GetLocalBBoxFresh(fi, cw.InverseTransform);
                                if (bb == null) continue;

                                var pc = GetCenter(bb);
                                double dx = pc.X - c.corner.X;
                                double dz = pc.Z - c.corner.Z;
                                double d2 = dx * dx + dz * dz;

                                if (d2 < bestDist)
                                {
                                    bestDist = d2;
                                    best = fi;
                                }
                            }

                            if (best != null)
                                foundCorners[c.name] = best;
                        }

                        if (foundCorners.Count == 0)
                            continue;

                        cutoutPanelsFound += foundCorners.Count;

                        void ApplyCorner(string cornerName, FamilyInstance cutoutFi)
                        {
                            if (cutoutFi == null) return;

                            bool isTopCorner = cornerName == "TL" || cornerName == "TR";
                            bool isLeftCorner = cornerName == "TL" || cornerName == "BL";

                            var widthSource = isLeftCorner ? bestLeft : bestRight;
                            var heightSource = isTopCorner ? bestTop : bestBottom;

                            if (widthSource == null || heightSource == null)
                                return;

                            if (!TryGetDoubleParam(widthSource, REG_PARAM_HOR, out double widthFt) || widthFt <= EPS)
                                return;

                            string heightParam = isTopCorner ? REG_PARAM_TOP_HEIGHT : REG_PARAM_BOTTOM_HEIGHT;
                            if (!TryGetDoubleParam(heightSource, heightParam, out double heightFt) || heightFt <= EPS)
                                return;

                            bool setW = TrySetParam(cutoutFi, CUT_PARAM_W, widthFt);
                            bool setH = TrySetParam(cutoutFi, CUT_PARAM_H, heightFt);

                            if (setW) paramsSet++;
                            if (setH) paramsSet++;
                            if (setW || setH) cutoutPanelsUpdated++;
                        }

                        foreach (var kv in foundCorners)
                            ApplyCorner(kv.Key, kv.Value);
                    }
                }

                _doc.Regenerate();
                t.Commit();
            }

            Debug.WriteLine($"{TAG} END: wallsProcessed={wallsProcessed}, openingsProcessed={openingsProcessed}, cutoutPanelsFound={cutoutPanelsFound}, cutoutPanelsUpdated={cutoutPanelsUpdated}, paramsSet={paramsSet}");
        }

        // ===========================
        // ===== EXISTING FEATURE =====
        // ===========================
        private void ReplaceRegularPanelsWithCutoutPanels(List<CurtainWallDataDto> data)
        {
            const string REGULAR_FAMILY = "КРСТ_НВФ_Рядовая_В3";
            const string CUTOUT_TOP_FAMILY = "КРСТ_НВФ_С Г-образным вырезом_В2";
            const string CUTOUT_BOTTOM_FAMILY = "КРСТ_НВФ_С L-образным вырезом";

            const double CHECK_SEGMENT_LENGTH_FT = 0.328084;
            const double PANEL_BBOX_REDUCTION_FACTOR = 0.70;

            Debug.WriteLine("[ReplaceRegularPanelsWithCutoutPanels] START");

            if (data == null || data.Count == 0)
            {
                Debug.WriteLine("[ReplaceRegularPanelsWithCutoutPanels] data is null/empty -> skip");
                return;
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

            var topSymbol = GetFamilySymbolByName(CUTOUT_TOP_FAMILY);
            var bottomSymbol = GetFamilySymbolByName(CUTOUT_BOTTOM_FAMILY);

            if (topSymbol == null || bottomSymbol == null)
            {
                Debug.WriteLine($"[ReplaceRegularPanelsWithCutoutPanels] ERROR: target symbols not found. Top null={topSymbol == null}, Bottom null={bottomSymbol == null}");
                TaskDialog.Show("Ошибка", "Не найдены семейства для замены угловых панелей (проверь имена семейств в проекте).");
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
            const string REGULAR_PANEL_FAMILY = "КРСТ_НВФ_Рядовая_В3";
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

            const double DELTA_MM = -43.0;
            const double VERTICAL_MM = 7.0;
            const double HORIZONTAL_MM = 55.0;

            double MmToFt(double mm) => mm / FEET_TO_MM;

            int totalPanelsTouched = 0;
            int totalParamsSet = 0;
            int totalOpeningsProcessed = 0;

            using (Transaction t = new Transaction(_doc, "CW: Set regular panel cuts by openings (local bbox)"))
            {
                t.Start();
                _doc.Regenerate();

                foreach (var cw in data)
                {
                    if (cw == null || cw.CurtainWallElement == null)
                        continue;

                    var wallId = cw.CurtainWallElement.Id.IntegerValue;
                    var openings = cw.IntersectingOpenings ?? new List<OpeningModelDto>();
                    var panelsAll = cw.Panels ?? new List<CurtainWallPanelDto>();

                    var regularPanels = panelsAll
                        .Where(p => p?.PanelElement != null && p.PanelElement.Symbol?.Family != null)
                        .Where(p => p.PanelElement.Symbol.Family.Name == "КРСТ_НВФ_Рядовая_В3")
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
                                    adjustedValueFt = baseValueFt + MmToFt(VERTICAL_MM + DELTA_MM);
                                }
                                else
                                {
                                    paramName = "Подрезка_Верх";
                                    baseValueFt = OverlapZ(opBox, pBox);
                                    adjustedValueFt = baseValueFt - MmToFt(VERTICAL_MM) + MmToFt(DELTA_MM);
                                }
                            }
                            else
                            {
                                paramName = "Подрезка";
                                baseValueFt = OverlapX(opBox, pBox);
                                adjustedValueFt = baseValueFt - MmToFt(HORIZONTAL_MM) + MmToFt(DELTA_MM);
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

                _doc.Regenerate();
                t.Commit();
            }

            Debug.WriteLine($"[CalculateAndSetRegularPanelsCuts] END: openingsProcessed={totalOpeningsProcessed}, panelsTouched={totalPanelsTouched}, paramsSet={totalParamsSet}");
        }

        // ===========================
        // ===== DATA COLLECTION ======
        // ===========================
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
