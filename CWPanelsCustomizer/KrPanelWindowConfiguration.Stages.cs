using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CWPanelsCustomizer.Helpers;

namespace CWPanelsCustomizer
{
    public partial class CurtainPanelWindowConfiguration
    {
        // ==========================================================
        // === NEW FEATURE: MIRROR PANELS RIGHT OF OPENING (BY BB) ===
        // ==========================================================
        private void MirrorPanelsRightOfOpenings(List<CurtainWallDataDto> data)
        {
            // v6: Local X side detection (как в рабочем методе) + правила для L / Г_В2 / Рядовая_В3

            const string TAG = "[MirrorPanelsRightOfOpenings v6_LocalRules]";
            const double SIDE_TOL_MM = 1.0;     // панели на оси окна не трогаем
            const double BAND_EXPAND_MM = 5.0;  // слегка расширяем bbox окна, чтобы увереннее ловить пересечение

            // Ключи типов панелей
            const string L_PANEL_KEY = L_PANEL_FAMILY_NAME;
            const string G_PANEL_KEY = G_PANEL_FAMILY_NAME;
            const string REG_PANEL_KEY = REGULAR_PANEL_FAMILY_NAME;

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

            using (var t = new Transaction(_doc, "CW: Mirror panels by opening side (Local + Rules)"))
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

                        var obLocal = ExpandBBoxXZ(obLocalFresh, bandExpandFt);
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
                                    continue;
                                }

                                //_doc.Regenerate();
                                flippedOk++;
                            }
                            catch (Exception ex)
                            {
                                flipErrors++;
                                _logger.Info($"{TAG} ERROR flip wallId={wallId} openingId={opId} panelId={fi.Id.IntegerValue}: {ex}");
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
            int wallsVisited = 0;
            int wallsSkippedNoOpenings = 0;
            int wallsSkippedNoRegular = 0;
            int openingsSkippedNullBBox = 0;
            int openingsSkippedNoCandidates = 0;
            int openingsSkippedNoCornerHits = 0;
            int totalRegularPanels = 0;
            int totalCandidates = 0;
            int totalCornerCommonHits = 0;

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

                    wallsVisited++;
                    int wallId = wallData.CurtainWallElement.Id.IntegerValue;
                    var openings = wallData.IntersectingOpenings ?? new List<OpeningModelDto>();
                    var panels = wallData.Panels ?? new List<CurtainWallPanelDto>();

                    var regularPanels = panels
                        .Where(p => p?.PanelElement != null)
                        .Where(p => p.PanelElement.Symbol?.Family?.Name?.Contains(REGULAR_FAMILY) == true)
                        .ToList();
                    totalRegularPanels += regularPanels.Count;

                    _logger.Info($"[ReplaceRegularPanelsWithCutoutPanels] wallId={wallId} openings={openings.Count} panels={panels.Count} regularPanels={regularPanels.Count}");

                    if (openings.Count == 0)
                    {
                        wallsSkippedNoOpenings++;
                        continue;
                    }

                    if (regularPanels.Count == 0)
                    {
                        wallsSkippedNoRegular++;
                        continue;
                    }

                    foreach (var opening in openings)
                    {
                        if (opening?.OpeningElement == null)
                            continue;

                        int openingId = opening.OpeningElement.Id.IntegerValue;
                        var ob = opening.LocalBoundingBox;
                        if (ob == null)
                        {
                            openingsSkippedNullBBox++;
                            _logger.Info($"[ReplaceRegularPanelsWithCutoutPanels] wallId={wallId} openingId={openingId} skip: opening local bbox is null");
                            continue;
                        }

                        openingsProcessed++;

                        var candidate = new List<(FamilyInstance fi, BoundingBoxXYZ bbox)>();
                        foreach (var p in regularPanels)
                        {
                            var pb = p.LocalBoundingBox;
                            if (pb == null) continue;

                            var reduced = ReduceBBox(pb, PANEL_BBOX_REDUCTION_FACTOR);
                            if (Intersects3D(ob, reduced))
                                candidate.Add((p.PanelElement, reduced));
                        }
                        totalCandidates += candidate.Count;

                        _logger.Info($"[ReplaceRegularPanelsWithCutoutPanels] wallId={wallId} openingId={openingId} regularPanels={regularPanels.Count} candidates={candidate.Count} " +
                                     $"openingLocalX=({ob.Min.X * FEET_TO_MM:F0}..{ob.Max.X * FEET_TO_MM:F0})mm openingLocalZ=({ob.Min.Z * FEET_TO_MM:F0}..{ob.Max.Z * FEET_TO_MM:F0})mm");

                        if (candidate.Count == 0)
                        {
                            openingsSkippedNoCandidates++;
                            continue;
                        }

                        var windowCornerTL = new XYZ(ob.Min.X, 0, ob.Max.Z);
                        var windowCornerTR = new XYZ(ob.Max.X, 0, ob.Max.Z);
                        var windowCornerBL = new XYZ(ob.Min.X, 0, ob.Min.Z);
                        var windowCornerBR = new XYZ(ob.Max.X, 0, ob.Min.Z);

                        var corners = new List<(string name, XYZ corner, XYZ dirV, XYZ dirH)>
                {
                    ("TL", windowCornerTL, new XYZ(0,0, 1), new XYZ(-1,0,0)),
                    ("TR", windowCornerTR, new XYZ(0,0, 1), new XYZ( 1,0,0)),
                    ("BL", windowCornerBL, new XYZ(0,0,-1), new XYZ(-1,0,0)),
                    ("BR", windowCornerBR, new XYZ(0,0,-1), new XYZ( 1,0,0)),
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
                            totalCornerCommonHits += common.Count;

                            _logger.Info($"[ReplaceRegularPanelsWithCutoutPanels] wallId={wallId} openingId={openingId} corner={c.name} hitV={hitV.Count} hitH={hitH.Count} common={common.Count} commonIds={string.Join(",", common.Select(fi => fi.Id.IntegerValue))}");

                            foreach (var fi in common)
                                panelsToReplace.Add(fi);
                        }

                        _logger.Info($"[ReplaceRegularPanelsWithCutoutPanels] wallId={wallId} openingId={openingId} panelsToReplace={panelsToReplace.Count} ids={string.Join(",", panelsToReplace.Select(fi => fi.Id.IntegerValue))}");

                        if (panelsToReplace.Count == 0)
                        {
                            openingsSkippedNoCornerHits++;
                            continue;
                        }

                        var windowCenter = CenterOf(ob);

                        foreach (var panelFi in panelsToReplace)
                        {
                            if (panelFi == null) continue;
                            if (alreadyReplaced.Contains(panelFi.Id)) continue;

                            var pbDto = regularPanels.FirstOrDefault(x => x.PanelElement?.Id == panelFi.Id)?.LocalBoundingBox;
                            if (pbDto == null) continue;

                            var panelCenter = CenterOf(pbDto);
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
                                _logger.Info($"[ReplaceRegularPanelsWithCutoutPanels] REPLACED wallId={wallId} openingId={openingId} panelId={panelFi.Id.IntegerValue} " +
                                             $"isTop={isTop} targetFamily='{target.FamilyName}' targetType='{target.Name}'");
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

            _logger.Info($"[ReplaceRegularPanelsWithCutoutPanels] END wallsVisited={wallsVisited}, openingsProcessed={openingsProcessed}, replaced={replaced}, " +
                         $"wallsSkippedNoOpenings={wallsSkippedNoOpenings}, wallsSkippedNoRegular={wallsSkippedNoRegular}, " +
                         $"openingsSkippedNullBBox={openingsSkippedNullBBox}, openingsSkippedNoCandidates={openingsSkippedNoCandidates}, openingsSkippedNoCornerHits={openingsSkippedNoCornerHits}, " +
                         $"totalRegularPanels={totalRegularPanels}, totalCandidates={totalCandidates}, totalCornerCommonHits={totalCornerCommonHits}");
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

    }
}
