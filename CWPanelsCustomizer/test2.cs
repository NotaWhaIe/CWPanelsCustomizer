using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using Autodesk.Revit.UI;
using CWPanelsCustomizer.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace CWPanelsCustomizer
{
    [Transaction(TransactionMode.Manual)]
    public class test2 : IExternalCommand
    {
        public static string IS_NAME => "Размещение стоек";
        public static string IS_DESCRIPTION => "Размещение семейства стоек по вертикальным линиям витража";
        public static string IS_TAB_NAME => "#BIM";
        public static string IS_IMAGE => "CWPanelsCustomizer.Images.a1.png";

        private SphereByPoint _sphereByPoint;
        private UIDocument _uidoc;
        private Document _doc;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            _uidoc = commandData.Application.ActiveUIDocument;
            _doc = _uidoc.Document;
            _sphereByPoint = new SphereByPoint(_doc);

            using (TransactionGroup tg = new TransactionGroup(_doc, IS_NAME))
            {
                tg.Start();

                Method();

                tg.Assimilate();
            }

            return Result.Succeeded;
        }

        private void Method()
        {
            Debug.WriteLine("=== test2.Method: START ===");

            View activeView = _doc.ActiveView;
            Debug.WriteLine("ActiveView: Id=" + activeView.Id.IntegerValue + ", Name=" + activeView.Name + ", Type=" + activeView.ViewType);

            View3D view3D = activeView as View3D;
            if (view3D == null)
            {
                Debug.WriteLine("ERROR: Активный вид не является 3D видом.");
                throw new InvalidOperationException("Команда должна запускаться на 3D виде.");
            }

            SketchPlane sketchPlane = view3D.SketchPlane;
            if (sketchPlane == null)
            {
                Debug.WriteLine("ERROR: На 3D виде не задана рабочая плоскость (SketchPlane == null).");
                throw new InvalidOperationException("На активном 3D виде должна быть заранее настроена рабочая плоскость.");
            }

            Plane plane = sketchPlane.GetPlane();
            Debug.WriteLine("SketchPlane: Id=" + sketchPlane.Id.IntegerValue + ", Origin=" + FormatXyz(plane.Origin) + ", Normal=" + FormatXyz(plane.Normal));

            const string familyName = "КРСТ_НВФ_ZIAS_Массив стоек с кронштейнами_В2";
            const string symbolName = "187";

            FamilySymbol symbol = FindFamilySymbolByNames(_doc, familyName, symbolName);
            if (symbol == null)
            {
                Debug.WriteLine("ERROR: Не найден FamilySymbol. FamilyName='" + familyName + "', TypeName='" + symbolName + "'.");
                throw new InvalidOperationException("Не найдено семейство/тип: " + familyName + " : " + symbolName);
            }

            Debug.WriteLine("FamilySymbol: SymbolId=" + symbol.Id.IntegerValue + ", Family='" + symbol.FamilyName + "', Type='" + symbol.Name + "'");

            XYZ insertionPoint = plane.Origin;

            // 1 мм в футах
            double toleranceFeet = 0.00328084;

            using (Transaction t = new Transaction(_doc, "Place rack instance"))
            {
                t.Start();

                FailureHandlingOptions fho = t.GetFailureHandlingOptions();
                fho.SetFailuresPreprocessor(new DuplicateInstancesWarningSuppressor());
                t.SetFailureHandlingOptions(fho);

                if (!symbol.IsActive)
                {
                    Debug.WriteLine("Symbol не активен -> Activate()");
                    symbol.Activate();
                    _doc.Regenerate();
                }

                Debug.WriteLine("Place: Point=" + FormatXyz(insertionPoint));

                FamilyInstance newInstance = _doc.Create.NewFamilyInstance(insertionPoint, symbol, sketchPlane, StructuralType.NonStructural);
                Debug.WriteLine("Created: InstanceId=" + newInstance.Id.IntegerValue);

                // Фактическая “позиция” экземпляра (LocationPoint если есть, иначе центр bounding-box)
                XYZ newPos = GetInstanceEffectivePosition(newInstance);
                Debug.WriteLine("NewInstance EffectivePosition=" + FormatXyz(newPos));

                // Ищем, есть ли уже другой экземпляр ЭТОГО ЖЕ ТИПА в той же фактической позиции
                ElementId duplicateId = FindDuplicateInstanceIdByEffectivePosition(_doc, symbol, newInstance.Id, newPos, toleranceFeet);

                if (duplicateId != ElementId.InvalidElementId)
                {
                    Debug.WriteLine("DUPLICATE DETECTED: ExistingInstanceId=" + duplicateId.IntegerValue + " -> deleting newly created InstanceId=" + newInstance.Id.IntegerValue);
                    _doc.Delete(newInstance.Id);
                }
                else
                {
                    Debug.WriteLine("No duplicate detected for InstanceId=" + newInstance.Id.IntegerValue);
                }

                t.Commit();
            }

            Debug.WriteLine("=== test2.Method: END ===");
        }

        private static XYZ GetInstanceEffectivePosition(FamilyInstance instance)
        {
            if (instance == null) return null;

            LocationPoint lp = instance.Location as LocationPoint;
            if (lp != null && lp.Point != null)
            {
                return lp.Point;
            }

            BoundingBoxXYZ bb = instance.get_BoundingBox(null);
            if (bb != null && bb.Min != null && bb.Max != null)
            {
                return (bb.Min + bb.Max) * 0.5;
            }

            return XYZ.Zero;
        }

        private static ElementId FindDuplicateInstanceIdByEffectivePosition(
            Document doc,
            FamilySymbol symbol,
            ElementId excludeInstanceId,
            XYZ targetPos,
            double toleranceFeet)
        {
            if (doc == null || symbol == null || targetPos == null) return ElementId.InvalidElementId;

            FilteredElementCollector collector = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilyInstance));

            foreach (FamilyInstance fi in collector)
            {
                if (fi == null) continue;
                if (fi.Id == excludeInstanceId) continue;

                FamilySymbol fiSymbol = fi.Symbol;
                if (fiSymbol == null) continue;

                if (fiSymbol.Id.IntegerValue != symbol.Id.IntegerValue) continue;

                XYZ pos = GetInstanceEffectivePosition(fi);
                if (pos == null) continue;

                double dist = pos.DistanceTo(targetPos);
                if (dist <= toleranceFeet)
                {
                    return fi.Id;
                }
            }

            return ElementId.InvalidElementId;
        }

        private class DuplicateInstancesWarningSuppressor : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                IList<FailureMessageAccessor> failures = failuresAccessor.GetFailureMessages();
                if (failures == null || failures.Count == 0)
                {
                    return FailureProcessingResult.Continue;
                }

                foreach (FailureMessageAccessor fma in failures)
                {
                    if (fma == null) continue;

                    string text = fma.GetDescriptionText();
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    // Локализованный текст предупреждения (как у тебя)
                    if (text.Contains("В одном и том же месте имеются идентичные экземпляры"))
                    {
                        Debug.WriteLine("SUPPRESS WARNING: " + text);
                        failuresAccessor.DeleteWarning(fma);
                    }
                }

                return FailureProcessingResult.Continue;
            }
        }

        private static FamilySymbol FindFamilySymbolByNames(Document doc, string familyName, string symbolName)
        {
            Debug.WriteLine("FindFamilySymbolByNames: FamilyName='" + familyName + "', TypeName='" + symbolName + "'");

            FilteredElementCollector collector = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol));

            foreach (FamilySymbol fs in collector)
            {
                if (string.Equals(fs.FamilyName, familyName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(fs.Name, symbolName, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine("FindFamilySymbolByNames: MATCH. SymbolId=" + fs.Id.IntegerValue);
                    return fs;
                }
            }

            Debug.WriteLine("FindFamilySymbolByNames: NOT FOUND.");
            return null;
        }

        private static string FormatXyz(XYZ p)
        {
            if (p == null) return "<null>";
            return "(" + p.X.ToString("F6") + ", " + p.Y.ToString("F6") + ", " + p.Z.ToString("F6") + ")";
        }
    }
}
