using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using CWPanelsCustomizer.Helpers;

namespace CWPanelsCustomizer
{
    public partial class CurtainPanelWindowConfiguration
    {
        private List<CurtainWallDataDto> GetElements(Document doc)
        {
            _logger.Info("[CWPanelsCustomizer] GetElements START");

            List<Wall> allCurtainWalls = new List<Wall>(_curtainWalls);

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
                    if (wallBboxesWorld.TryGetValue(cw.Id, out BoundingBoxXYZ wb) && wb != null && Intersects3D(wb, openingBboxWorld))
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
                    // В режиме ByPanels — обрабатываем только выделенные панели (и их новые Id после ChangeTypeId)
                    if (_selMode == PluginSelectionMode.ByPanels && _selectedPanelIds != null
                        && !_selectedPanelIds.Contains(pid))
                        continue;

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

        private int GetTotalCurtainWallsCount(Document doc) => _curtainWalls.Count;

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
                // Поиск сначала в кеше по началу ключа "familyName/"
                if (_symbolCache != null)
                {
                    var match = _symbolCache
                        .FirstOrDefault(kv => kv.Key.StartsWith(familyName + "/", StringComparison.OrdinalIgnoreCase));
                    if (match.Value != null)
                        return match.Value;
                }
                // Fallback: Family → GetFamilySymbolIds
                var family = new FilteredElementCollector(_doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault(f => f.Name == familyName);
                if (family == null) return null;
                var symbolIds = family.GetFamilySymbolIds();
                if (symbolIds == null || symbolIds.Count == 0) return null;
                return _doc.GetElement(symbolIds.First()) as FamilySymbol;
            }
            catch { return null; }
        }

        // ===========================
        // ===== SHARED HELPERS ======
        // ===========================

        /// <summary>
        /// Перенос параметров КР-панели: undo-запись, Угол_Слева=0, смещение, материал.
        /// Вызывается из TX1 (FI-панели с неизменным Id) и TX2 (Wall→FI, после матчинга).
        /// </summary>
        private void TransferKrParameters(
            Element krElement, double offsetFt, int materialIdInt, int origArTypeId,
            Document doc, string tag,
            ref int offsetsTransferred, ref int offsetsFailed,
            ref int materialsTransferred, ref int materialsFailed,
            ref int materialParam2Transferred, ref int materialParam2Failed)
        {
            const string KR_OFFSET_PARAM   = "Смещение от плоскости фасада";
            const string KR_COLOR_PARAM    = "Цвет по шкале RAL (н/в)";
            const string KR_MATERIAL_PARAM = "Кассета_Материал отделки";
            const string KR_ANGLE_PARAM    = "Угол_Слева";

            int elemId = krElement.Id.IntegerValue;

            // Undo-запись (даже при нулевом смещении — для отката типа AR→KR)
            _undoRecord.Add((elemId, origArTypeId));

            // Угол_Слева = 0 — инициализация перед прочими параметрами
            Parameter krAngleP = krElement.LookupParameter(KR_ANGLE_PARAM);
            if (krAngleP != null && !krAngleP.IsReadOnly)
                krAngleP.Set(0.0);

            // Перенос смещения + коррекция 13 мм (неточность семейства КР-панели)
            double correctedOffsetFt = offsetFt + MmToFt(KR_FAMILY_OFFSET_CORRECTION_MM);
            Parameter krOffsetParam = krElement.LookupParameter(KR_OFFSET_PARAM);
            if (krOffsetParam != null && !krOffsetParam.IsReadOnly)
            {
                krOffsetParam.Set(correctedOffsetFt);
                offsetsTransferred++;
                _logger.Info($"{tag} [OFFSET] Id={elemId} base={offsetFt * FEET_TO_MM:F0}mm+13mm={correctedOffsetFt * FEET_TO_MM:F0}mm ok");
            }
            else
            {
                offsetsFailed++;
                _logger.Info($"{tag} [OFFSET-FAIL] Id={elemId} paramFound={krOffsetParam != null}");
            }

            // Перенос материала
            if (materialIdInt > 0)
            {
                Material mat = doc.GetElement(new ElementId(materialIdInt)) as Material;
                Parameter krColorP = krElement.LookupParameter(KR_COLOR_PARAM);
                if (mat != null && krColorP != null && !krColorP.IsReadOnly && krColorP.StorageType == StorageType.String)
                {
                    krColorP.Set(mat.Name);
                    materialsTransferred++;
                    _logger.Info($"{tag} [MAT] Id={elemId} mat='{mat.Name}' ok");
                }
                else
                {
                    materialsFailed++;
                    _logger.Info($"{tag} [MAT-FAIL] Id={elemId} matId={materialIdInt} paramFound={krColorP != null} mat={mat != null}");
                }

                Parameter krMatP = krElement.LookupParameter(KR_MATERIAL_PARAM);
                if (krMatP != null && !krMatP.IsReadOnly)
                {
                    if (krMatP.StorageType == StorageType.ElementId)
                    {
                        krMatP.Set(new ElementId(materialIdInt));
                        materialParam2Transferred++;
                        _logger.Info($"{tag} [MAT2] Id={elemId} matId={materialIdInt} ok");
                    }
                    else if (krMatP.StorageType == StorageType.String && mat != null)
                    {
                        krMatP.Set(mat.Name);
                        materialParam2Transferred++;
                        _logger.Info($"{tag} [MAT2] Id={elemId} mat='{mat.Name}' ok (string)");
                    }
                }
                else
                {
                    materialParam2Failed++;
                    _logger.Info($"{tag} [MAT2-FAIL] Id={elemId} matId={materialIdInt} paramFound={krMatP != null}");
                }
            }
        }

        private BoundingBoxXYZ ExpandBBoxXZ(BoundingBoxXYZ b, double expandFt)
        {
            if (b == null) return null;
            return new BoundingBoxXYZ
            {
                Min = new XYZ(b.Min.X - expandFt, b.Min.Y, b.Min.Z - expandFt),
                Max = new XYZ(b.Max.X + expandFt, b.Max.Y, b.Max.Z + expandFt)
            };
        }

        private BoundingBoxXYZ ReduceBBox(BoundingBoxXYZ b, double factor)
        {
            var c = CenterOf(b);
            double hx = (b.Max.X - b.Min.X) * 0.5 * factor;
            double hy = (b.Max.Y - b.Min.Y) * 0.5 * factor;
            double hz = (b.Max.Z - b.Min.Z) * 0.5 * factor;
            return new BoundingBoxXYZ
            {
                Min = new XYZ(c.X - hx, c.Y - hy, c.Z - hz),
                Max = new XYZ(c.X + hx, c.Y + hy, c.Z + hz)
            };
        }

        private FamilyInstance PickClosestByXZ(List<FamilyInstance> candidates, XYZ targetCornerXZ, Transform inv)
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
                if (d2 < bestD2) { bestD2 = d2; best = fi; }
            }
            return best;
        }

        private static bool PointInRect2D(XYZ p, double minX, double maxX, double minZ, double maxZ) =>
            p.X >= minX && p.X <= maxX && p.Z >= minZ && p.Z <= maxZ;

        private static double Cross2D(XYZ a, XYZ b, XYZ c)
        {
            double abx = b.X - a.X, abz = b.Z - a.Z;
            double acx = c.X - a.X, acz = c.Z - a.Z;
            return abx * acz - abz * acx;
        }

        private static bool SegmentsIntersect2D(XYZ a, XYZ b, XYZ c, XYZ d)
        {
            const double E = 1e-9;
            double d1 = Cross2D(a, b, c), d2 = Cross2D(a, b, d);
            double d3 = Cross2D(c, d, a), d4 = Cross2D(c, d, b);
            bool Proper = ((d1 > E && d2 < -E) || (d1 < -E && d2 > E)) &&
                          ((d3 > E && d4 < -E) || (d3 < -E && d4 > E));
            if (Proper) return true;
            bool OnSeg(XYZ p, XYZ q, XYZ r) =>
                q.X >= Math.Min(p.X, r.X) - E && q.X <= Math.Max(p.X, r.X) + E &&
                q.Z >= Math.Min(p.Z, r.Z) - E && q.Z <= Math.Max(p.Z, r.Z) + E;
            bool Collinear(double val) => Math.Abs(val) <= E;
            if (Collinear(d1) && OnSeg(a, c, b)) return true;
            if (Collinear(d2) && OnSeg(a, d, b)) return true;
            if (Collinear(d3) && OnSeg(c, a, d)) return true;
            if (Collinear(d4) && OnSeg(c, b, d)) return true;
            return false;
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
            var r1 = new XYZ(minX, 0, minZ); var r2 = new XYZ(maxX, 0, minZ);
            var r3 = new XYZ(maxX, 0, maxZ); var r4 = new XYZ(minX, 0, maxZ);
            if (SegmentsIntersect2D(p1, p2, r1, r2)) return true;
            if (SegmentsIntersect2D(p1, p2, r2, r3)) return true;
            if (SegmentsIntersect2D(p1, p2, r3, r4)) return true;
            if (SegmentsIntersect2D(p1, p2, r4, r1)) return true;
            return false;
        }

        private List<FamilyInstance> GetHitPanelsBySegment2D(
            List<(FamilyInstance fi, BoundingBoxXYZ bbox)> panels, XYZ s1, XYZ s2)
        {
            var res = new List<FamilyInstance>();
            foreach (var p in panels)
                if (SegmentIntersectsRect2D(s1, s2, p.bbox))
                    res.Add(p.fi);
            return res;
        }

        private FamilySymbol GetFamilySymbolByFamilyAndType(string familyName, string typeName)
        {
            if (string.IsNullOrWhiteSpace(familyName) || string.IsNullOrWhiteSpace(typeName))
                return null;
            string fn = familyName.Trim(), tn = typeName.Trim();
            // Сначала поиск по кешу (быстро)
            if (_symbolCache != null)
            {
                string key = $"{fn}/{tn}";
                if (_symbolCache.TryGetValue(key, out var cached))
                    return cached;
                // Fallback: поиск без учёта регистра в кеше
                return _symbolCache.Values.FirstOrDefault(s =>
                    string.Equals(s.Family?.Name?.Trim(), fn, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.Name?.Trim(), tn, StringComparison.OrdinalIgnoreCase));
            }
            // Fallback: FilteredElementCollector (если кеш не инициализирован)
            return new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .FirstOrDefault(s =>
                {
                    var fam = s?.Family?.Name?.Trim();
                    var typ = s?.Name?.Trim();
                    return !string.IsNullOrEmpty(fam) && !string.IsNullOrEmpty(typ)
                           && fam.Equals(fn, StringComparison.OrdinalIgnoreCase)
                           && typ.Equals(tn, StringComparison.OrdinalIgnoreCase);
                });
        }

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
            // Поиск сначала в кеше (быстро)
            FamilySymbol sym = _symbolCache?.Values
                .FirstOrDefault(fs => fs.FamilyName?.IndexOf("Пустая", StringComparison.OrdinalIgnoreCase) >= 0);
            // Fallback: FilteredElementCollector
            if (sym == null)
                sym = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
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
