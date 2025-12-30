using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using CWPanelsCustomizer.Helpers;
using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CWPanelsCustomizer
{
    public class CurtainGridGenerator : IExternalCommand
    {
        public static string IS_NAME => "Нарезать витраж";
        public static string IS_DESCRIPTION => "*Что делает плагин?";

        public static string IS_TAB_NAME => "#BIM";
        public static string IS_IMAGE => "CWPanelsCustomizer.Images.a1.png";

        private SphereByPoint _sphereByPoint;
        private UIDocument _uidoc;
        private Document _doc;

        // Настройки разрезки (мм)
        public double PanelHeight_mm { get; set; } = 1000.0;
        public double PanelWidth_mm { get; set; } = 2000.0;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            _uidoc = commandData.Application.ActiveUIDocument;
            _doc = _uidoc.Document;
            _sphereByPoint = new SphereByPoint(_doc);

            bool start = ShowSettingsDialog_FirstInputBox();
            if (!start)
            {
                return Result.Cancelled;
            }

            using (TransactionGroup tg = new TransactionGroup(_doc, IS_NAME))
            {
                tg.Start();
                Method();
                tg.Assimilate();
            }

            return Result.Succeeded;
        }

        /// <summary>
        /// Первое окно — VisualBasic InputBox (ввод двух чисел).
        /// Затем — TaskDialog со Start/пере-ввод/Cancel.
        /// </summary>
        private bool ShowSettingsDialog_FirstInputBox()
        {
            // 1) Сразу просим два числа в одном InputBox
            if (!PromptBothValuesWithInputBox())
            {
                return false; // Cancel или неверный ввод
            }

            // 2) Затем — короткое окно подтверждения
            while (true)
            {
                TaskDialog td = new TaskDialog(IS_NAME);
                td.MainInstruction = "Настройки нарезки витража";
                td.MainContent =
                    $"Параметры (мм):\n" +
                    $"• Высота панели: {PanelHeight_mm.ToString("0.##", CultureInfo.InvariantCulture)}\n" +
                    $"• Ширина панели: {PanelWidth_mm.ToString("0.##", CultureInfo.InvariantCulture)}\n\n" +
                    "Нажмите Start для запуска или 'Пере-ввести', чтобы изменить значения.";

                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Start");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Пере-ввести числа");
                td.CommonButtons = TaskDialogCommonButtons.Cancel;

                TaskDialogResult r = td.Show();

                if (r == TaskDialogResult.Cancel)
                {
                    return false;
                }

                if (r == TaskDialogResult.CommandLink1)
                {
                    if (PanelHeight_mm <= 0.0 || PanelWidth_mm <= 0.0)
                    {
                        TaskDialog.Show(IS_NAME, "Ширина и высота панели должны быть больше 0 мм.");
                        continue;
                    }

                    return true;
                }

                if (r == TaskDialogResult.CommandLink2)
                {
                    if (!PromptBothValuesWithInputBox())
                    {
                        return false;
                    }
                }
            }
        }

        /// <summary>
        /// Один InputBox, в котором вводятся 2 числа: высота;ширина (мм).
        /// </summary>
        private bool PromptBothValuesWithInputBox()
        {
            string defaultText =
                PanelHeight_mm.ToString("0.##", CultureInfo.InvariantCulture) + ";" +
                PanelWidth_mm.ToString("0.##", CultureInfo.InvariantCulture);

            string prompt =
                "Введите два числа в мм:\n" +
                "Высота;Ширина\n\n" +
                "Примеры:\n" +
                "1000;2000\n" +
                "или на двух строках:\n" +
                "1000\n2000\n\n" +
                "Можно использовать запятую или точку для дробных.";

            string text = Interaction.InputBox(prompt, IS_NAME, defaultText);

            if (string.IsNullOrWhiteSpace(text))
            {
                return false; // Cancel / пусто
            }

            if (!TryParseTwoDoublesMm(text, out double h, out double w, out string error))
            {
                TaskDialog.Show(IS_NAME, error);
                return PromptBothValuesWithInputBox(); // повторный ввод (минимум кода, но удобно)
            }

            PanelHeight_mm = h;
            PanelWidth_mm = w;
            return true;
        }

        private static bool TryParseTwoDoublesMm(string text, out double h, out double w, out string error)
        {
            h = 0.0;
            w = 0.0;
            error = null;

            string s = text.Trim();

            // Разделители между двумя числами: ; или перевод строки
            string[] parts = s
                .Split(new[] { ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();

            if (parts.Length < 2)
            {
                error = "Нужно ввести два числа: Высота;Ширина (например 1000;2000).";
                return false;
            }

            if (!TryParseDoubleFlexible(parts[0], out h))
            {
                error = $"Не удалось распознать высоту: \"{parts[0]}\"";
                return false;
            }

            if (!TryParseDoubleFlexible(parts[1], out w))
            {
                error = $"Не удалось распознать ширину: \"{parts[1]}\"";
                return false;
            }

            if (h <= 0.0 || w <= 0.0)
            {
                error = "Высота и ширина должны быть больше 0 мм.";
                return false;
            }

            return true;
        }

        private static bool TryParseDoubleFlexible(string s, out double value)
        {
            value = 0.0;
            if (string.IsNullOrWhiteSpace(s))
            {
                return false;
            }

            // поддержка и "," и "."
            string normalized = s.Trim().Replace(',', '.');

            return double.TryParse(
                normalized,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value
            );
        }

        // ===================== БИЗНЕС-ЛОГИКА (НЕ ТРОГАЛ) =====================

        private void Method()
        {
            IList<ElementId> selectedIds = _uidoc.Selection.GetElementIds().ToList();
            if (selectedIds.Count != 1)
            {
                TaskDialog.Show(IS_NAME, "Выберите один витраж (Curtain Wall).");
                return;
            }

            Wall wall = _doc.GetElement(selectedIds[0]) as Wall;
            if (wall == null)
            {
                TaskDialog.Show(IS_NAME, "Выбранный элемент не является стеной витража (Curtain Wall).");
                return;
            }

            CurtainGrid grid = wall.CurtainGrid;
            if (grid == null)
            {
                TaskDialog.Show(IS_NAME, "Curtain Grid отсутствует. Выбранный элемент не является витражом.");
                return;
            }

            PlanarFace face = GetMainVerticalPlanarFace(wall);
            if (face == null)
            {
                TaskDialog.Show(IS_NAME, "Не удалось получить плоскость витража (PlanarFace) из геометрии.");
                return;
            }

            BoundingBoxXYZ bbox = wall.get_BoundingBox(null);
            if (bbox == null)
            {
                TaskDialog.Show(IS_NAME, "BoundingBox отсутствует.");
                return;
            }

            if (PanelHeight_mm <= 0.0 || PanelWidth_mm <= 0.0)
            {
                TaskDialog.Show(IS_NAME, "Ширина и высота панели должны быть больше 0 мм.");
                return;
            }

            double stepWidth = UnitUtils.ConvertToInternalUnits(PanelHeight_mm, UnitTypeId.Millimeters);
            double stepHeight = UnitUtils.ConvertToInternalUnits(PanelWidth_mm, UnitTypeId.Millimeters);

            XYZ planeOrigin = face.Origin;
            XYZ planeNormal = SafeNormalize(face.FaceNormal);
            if (planeNormal == null)
            {
                TaskDialog.Show(IS_NAME, "Не удалось получить нормаль плоскости витража.");
                return;
            }

            Line uLine = GetFirstGridLine(_doc, grid.GetUGridLineIds());
            Line vLine = GetFirstGridLine(_doc, grid.GetVGridLineIds());

            XYZ dU;
            XYZ dV;

            if (uLine != null && vLine != null)
            {
                dU = SafeNormalize(uLine.Direction);
                dV = SafeNormalize(vLine.Direction);

                if (dU == null || dV == null)
                {
                    TaskDialog.Show(IS_NAME, "Не удалось получить направления U/V линий сетки.");
                    return;
                }

                dU = ProjectDirectionToPlane(dU, planeNormal);
                dV = ProjectDirectionToPlane(dV, planeNormal);

                if (dU == null || dV == null)
                {
                    TaskDialog.Show(IS_NAME, "Направления U/V не удалось спроецировать в плоскость витража.");
                    return;
                }
            }
            else
            {
                LocationCurve lc = wall.Location as LocationCurve;
                Line baseLine = lc != null ? lc.Curve as Line : null;
                if (baseLine == null)
                {
                    TaskDialog.Show(IS_NAME, "Не удалось определить направления: нет U/V линий и нет базовой линии стены.");
                    return;
                }

                XYZ baseDir = SafeNormalize(baseLine.Direction);
                if (baseDir == null)
                {
                    TaskDialog.Show(IS_NAME, "Не удалось определить направление базовой линии.");
                    return;
                }

                dU = ProjectDirectionToPlane(baseDir, planeNormal);
                if (dU == null)
                {
                    TaskDialog.Show(IS_NAME, "Не удалось построить направление в плоскости витража.");
                    return;
                }

                dV = SafeNormalize(planeNormal.CrossProduct(dU));
                if (dV == null)
                {
                    TaskDialog.Show(IS_NAME, "Не удалось построить второе направление в плоскости витража.");
                    return;
                }
            }

            XYZ sU = SafeNormalize(planeNormal.CrossProduct(dU));
            XYZ sV = SafeNormalize(planeNormal.CrossProduct(dV));
            if (sU == null || sV == null)
            {
                TaskDialog.Show(IS_NAME, "Не удалось построить направления шага в плоскости витража.");
                return;
            }

            XYZ[] corners =
            {
                new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Min.Z),
                new XYZ(bbox.Min.X, bbox.Min.Y, bbox.Max.Z),
                new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Min.Z),
                new XYZ(bbox.Min.X, bbox.Max.Y, bbox.Max.Z),
                new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Min.Z),
                new XYZ(bbox.Max.X, bbox.Min.Y, bbox.Max.Z),
                new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Min.Z),
                new XYZ(bbox.Max.X, bbox.Max.Y, bbox.Max.Z)
            };

            double minSU = double.MaxValue;
            double maxSU = double.MinValue;
            double minSV = double.MaxValue;
            double maxSV = double.MinValue;

            for (int i = 0; i < corners.Length; i++)
            {
                XYZ p = ProjectPointToPlane(corners[i], planeOrigin, planeNormal);
                XYZ v = p - planeOrigin;

                double a = v.DotProduct(sU);
                double b = v.DotProduct(sV);

                if (a < minSU) minSU = a;
                if (a > maxSU) maxSU = a;
                if (b < minSV) minSV = b;
                if (b > maxSV) maxSV = b;
            }

            if (maxSU - minSU < stepWidth * 0.5 || maxSV - minSV < stepHeight * 0.5)
            {
                TaskDialog.Show(IS_NAME, "Габариты витража слишком малы для разрезки заданным шагом.");
                return;
            }

            double midSU = (minSU + maxSU) * 0.5;
            double midSV = (minSV + maxSV) * 0.5;

            List<double> uOffsets = new List<double>();
            for (double a = minSU + stepWidth; a < maxSU - 1e-6; a += stepWidth)
            {
                uOffsets.Add(a);
            }

            List<double> vOffsets = new List<double>();
            for (double b = minSV + stepHeight; b < maxSV - 1e-6; b += stepHeight)
            {
                vOffsets.Add(b);
            }

            int success = 0;
            int fail = 0;

            using (Transaction t = new Transaction(_doc, "Curtain Grid Divide"))
            {
                t.Start();

                for (int i = 0; i < uOffsets.Count; i++)
                {
                    double a = uOffsets[i];
                    XYZ raw = planeOrigin + sU.Multiply(a) + sV.Multiply(midSV);
                    XYZ pos = ProjectPointToPlane(raw, planeOrigin, planeNormal);

                    if (TryAddGridLine(grid, true, pos)) success++;
                    else fail++;
                }

                for (int i = 0; i < vOffsets.Count; i++)
                {
                    double b = vOffsets[i];
                    XYZ raw = planeOrigin + sU.Multiply(midSU) + sV.Multiply(b);
                    XYZ pos = ProjectPointToPlane(raw, planeOrigin, planeNormal);

                    if (TryAddGridLine(grid, false, pos)) success++;
                    else fail++;
                }

                t.Commit();
            }

            TaskDialog.Show(IS_NAME, $"Готово.\nДобавлено линий: {success}\nОшибок: {fail}");
        }

        private static bool TryAddGridLine(CurtainGrid grid, bool isUGridLine, XYZ position)
        {
            try
            {
                grid.AddGridLine(isUGridLine, position, false);
                return true;
            }
            catch
            {
                try
                {
                    grid.AddGridLine(isUGridLine, position, true);
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static XYZ ProjectPointToPlane(XYZ point, XYZ planeOrigin, XYZ planeNormal)
        {
            XYZ v = point - planeOrigin;
            double dist = v.DotProduct(planeNormal);
            return point - planeNormal.Multiply(dist);
        }

        private static XYZ ProjectDirectionToPlane(XYZ direction, XYZ planeNormal)
        {
            XYZ d = direction - planeNormal.Multiply(direction.DotProduct(planeNormal));
            return SafeNormalize(d);
        }

        private static XYZ SafeNormalize(XYZ v)
        {
            if (v == null) return null;
            double len = v.GetLength();
            if (len < 1e-9) return null;
            return v.Divide(len);
        }

        private static Line GetFirstGridLine(Document doc, ICollection<ElementId> ids)
        {
            if (ids == null || ids.Count == 0) return null;

            foreach (ElementId id in ids)
            {
                CurtainGridLine gl = doc.GetElement(id) as CurtainGridLine;
                if (gl == null) continue;

                Line l = gl.FullCurve as Line;
                if (l != null) return l;
            }

            return null;
        }

        private static PlanarFace GetMainVerticalPlanarFace(Wall wall)
        {
            Options options = new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = true,
                DetailLevel = ViewDetailLevel.Fine
            };

            GeometryElement ge = wall.get_Geometry(options);
            if (ge == null) return null;

            XYZ wallNormal = wall.Orientation;
            wallNormal = wallNormal != null ? SafeNormalize(wallNormal) : null;
            if (wallNormal == null) return null;

            PlanarFace best = null;
            double bestArea = 0.0;

            foreach (GeometryObject go in ge)
            {
                ScanGeometryObject(go, wallNormal, ref best, ref bestArea);
            }

            return best;
        }

        private static void ScanGeometryObject(GeometryObject go, XYZ wallNormal, ref PlanarFace best, ref double bestArea)
        {
            GeometryInstance gi = go as GeometryInstance;
            if (gi != null)
            {
                GeometryElement inst = gi.GetInstanceGeometry();
                if (inst != null)
                {
                    foreach (GeometryObject igo in inst)
                    {
                        ScanGeometryObject(igo, wallNormal, ref best, ref bestArea);
                    }
                }
                return;
            }

            Solid solid = go as Solid;
            if (solid == null || solid.Faces == null || solid.Faces.Size == 0) return;

            foreach (Face f in solid.Faces)
            {
                PlanarFace pf = f as PlanarFace;
                if (pf == null) continue;

                XYZ n = SafeNormalize(pf.FaceNormal);
                if (n == null) continue;

                double verticality = Math.Abs(n.DotProduct(XYZ.BasisZ));
                if (verticality > 0.2) continue;

                double align = Math.Abs(n.DotProduct(wallNormal));
                if (align < 0.8) continue;

                double area = pf.Area;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = pf;
                }
            }
        }
    }
}
