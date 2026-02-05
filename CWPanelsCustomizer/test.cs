//using System;
//using System.Collections.Generic;
//using Autodesk.Revit.Attributes;
//using Autodesk.Revit.DB;
//using Autodesk.Revit.UI;

//namespace CWPanelsCustomizer.Helpers
//{
//    [Transaction(TransactionMode.Manual)]
//    public class test : IExternalCommand
//    {
//        public static string IS_NAME => "Создание сферы DirectShape";
//        public static string IS_DESCRIPTION => "Создает сферу в нулевых координатах";

//        public static string IS_TAB_NAME => "#BIM";
//        public static string IS_IMAGE => "CWPanelsCustomizer.Images.a1.png";

//        // Удалено, если класс SphereByPoint не предоставлен, логика внутри Method
//        // private SphereByPoint _sphereByPoint; 
//        private UIDocument _uidoc;
//        private Document _doc;

//        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
//        {
//            _uidoc = commandData.Application.ActiveUIDocument;
//            _doc = _uidoc.Document;

//            using (TransactionGroup tg = new TransactionGroup(_doc, IS_NAME))
//            {
//                tg.Start();
//                Method();
//                tg.Assimilate();
//            }

//            return Result.Succeeded;
//        }

//        private void Method()
//        {
//            // Открываем транзакцию для создания элементов
//            using (Transaction t = new Transaction(_doc, "Создать сферу"))
//            {
//                t.Start();

//                // 1. Параметры сферы (в футах)
//                double radius = 1000 / 304.8; // 500 мм
//                XYZ center = XYZ.Zero;       // Координаты (0,0,0)

//                // 2. Построение геометрии (дуга и осевая линия для вращения)
//                Frame frame = new Frame(center, XYZ.BasisX, XYZ.BasisY, XYZ.BasisZ);

//                // Создаем полукруг в плоскости XZ
//                Arc arc = Arc.Create(
//                    center - XYZ.BasisZ * radius,
//                    center + XYZ.BasisZ * radius,
//                    center + XYZ.BasisX * radius
//                );

//                Line axis = Line.CreateBound(arc.GetEndPoint(1), arc.GetEndPoint(0));

//                CurveLoop loop = new CurveLoop();
//                loop.Append(arc);
//                loop.Append(axis);

//                // 3. Создание Solid тела вращением полукруга на 360 градусов (2PI)
//                Solid sphereSolid = GeometryCreationUtilities.CreateRevolvedGeometry(
//                    frame,
//                    new List<CurveLoop> { loop },
//                    0,
//                    2 * Math.PI
//                );

//                // 4. Создание DirectShape
//                DirectShape ds = DirectShape.CreateElement(_doc, new ElementId(BuiltInCategory.OST_GenericModel));
//                ds.SetShape(new List<GeometryObject> { sphereSolid });
//                ds.Name = "Сфера_DS";

//                t.Commit();
//            }
//        }
//    }
//}
