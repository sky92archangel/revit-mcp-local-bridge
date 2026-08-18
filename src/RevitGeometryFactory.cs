using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitCommandBridge
{
    internal static class RevitGeometryFactory
    {
        public static IList<GeometryObject> CreateGeometry(
            IDictionary<string, object> arguments,
            SolidOptions options)
        {
            object rawGeometry = PlanValues.Get(arguments, "geometry", "solids", "primitives");
            List<Dictionary<string, object>> primitives = PlanValues.DictionaryList(rawGeometry, "geometry");
            if (primitives.Count == 0)
            {
                throw new BridgeCommandException("create_direct_shape.geometry 至少需要一个实体原语。");
            }
            if (primitives.Count > 500)
            {
                throw new BridgeCommandException("单个 DirectShape 最多允许 500 个实体原语。");
            }

            var geometry = new List<GeometryObject>();
            foreach (Dictionary<string, object> primitive in primitives)
            {
                string kind = PlanValues.String(primitive, null, "kind", "type");
                if (string.IsNullOrWhiteSpace(kind))
                {
                    throw new BridgeCommandException("几何原语缺少 kind。");
                }
                switch (kind.Trim().ToLowerInvariant())
                {
                    case "box":
                    case "cuboid":
                        geometry.Add(CreateBox(primitive, options));
                        break;
                    case "cylinder":
                    case "tube":
                        geometry.Add(CreateCylinder(primitive, options));
                        break;
                    case "extrusion":
                        geometry.Add(CreateExtrusion(primitive, options));
                        break;
                    default:
                        throw new BridgeCommandException("不支持几何原语 kind=“" + kind + "”。支持 box、cylinder、extrusion。");
                }
            }
            return geometry;
        }

        public static Dictionary<string, object> DescribeGeometry(IDictionary<string, object> arguments)
        {
            object rawGeometry = PlanValues.Get(arguments, "geometry", "solids", "primitives");
            List<Dictionary<string, object>> primitives = PlanValues.DictionaryList(rawGeometry, "geometry");
            var kinds = new List<string>();
            foreach (Dictionary<string, object> primitive in primitives)
            {
                string kind = PlanValues.String(primitive, null, "kind", "type");
                if (string.IsNullOrWhiteSpace(kind))
                {
                    throw new BridgeCommandException("几何原语缺少 kind。");
                }
                ValidatePrimitive(primitive, kind);
                kinds.Add(kind.Trim().ToLowerInvariant());
            }
            return new Dictionary<string, object>
            {
                { "primitive_count", primitives.Count },
                { "primitive_kinds", kinds.ToArray() }
            };
        }

        private static Solid CreateBox(IDictionary<string, object> values, SolidOptions options)
        {
            XYZ min = PlanValues.Point(values, "min");
            XYZ max = PlanValues.Point(values, "max");
            if (max.X <= min.X || max.Y <= min.Y || max.Z <= min.Z)
            {
                throw new BridgeCommandException("box.max 必须在 min 的三个方向都更大。");
            }
            var loop = new CurveLoop();
            XYZ p1 = new XYZ(min.X, min.Y, min.Z);
            XYZ p2 = new XYZ(max.X, min.Y, min.Z);
            XYZ p3 = new XYZ(max.X, max.Y, min.Z);
            XYZ p4 = new XYZ(min.X, max.Y, min.Z);
            loop.Append(Line.CreateBound(p1, p2));
            loop.Append(Line.CreateBound(p2, p3));
            loop.Append(Line.CreateBound(p3, p4));
            loop.Append(Line.CreateBound(p4, p1));
            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { loop },
                XYZ.BasisZ,
                max.Z - min.Z,
                options);
        }

        private static Solid CreateCylinder(IDictionary<string, object> values, SolidOptions options)
        {
            XYZ start = PlanValues.Point(values, "start");
            XYZ end = PlanValues.Point(values, "end");
            double diameterMm = PlanValues.RequireMillimeters(values, "diameter_mm", "diameter");
            if (diameterMm <= 0.0)
            {
                throw new BridgeCommandException("cylinder.diameter_mm 必须大于 0。");
            }
            return CreateCylinder(start, end, PlanValues.ToFeet(diameterMm / 2.0), options);
        }

        private static Solid CreateExtrusion(IDictionary<string, object> values, SolidOptions options)
        {
            List<Dictionary<string, object>> rawProfile = PlanValues.DictionaryList(
                PlanValues.Get(values, "profile"),
                "extrusion.profile");
            if (rawProfile.Count < 3)
            {
                throw new BridgeCommandException("extrusion.profile 至少需要 3 个点。");
            }
            XYZ direction = ReadDirection(values);
            double lengthMm = PlanValues.RequireMillimeters(values, "length_mm", "length");
            if (lengthMm <= 0.0)
            {
                throw new BridgeCommandException("extrusion.length_mm 必须大于 0。");
            }

            var loop = new CurveLoop();
            var points = new List<XYZ>();
            foreach (Dictionary<string, object> point in rawProfile)
            {
                points.Add(PointFromDictionary(point, "extrusion.profile[]"));
            }
            for (int index = 0; index < points.Count; index++)
            {
                XYZ start = points[index];
                XYZ end = points[(index + 1) % points.Count];
                if (start.DistanceTo(end) < 1e-8)
                {
                    throw new BridgeCommandException("extrusion.profile 不能包含重合的相邻点。");
                }
                loop.Append(Line.CreateBound(start, end));
            }
            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { loop },
                direction,
                PlanValues.ToFeet(lengthMm),
                options);
        }

        private static Solid CreateCylinder(XYZ start, XYZ end, double radiusFeet, SolidOptions options)
        {
            XYZ vector = end - start;
            double length = vector.GetLength();
            if (length < 1e-7)
            {
                throw new BridgeCommandException("cylinder.start 与 end 不能重合。");
            }
            XYZ direction = vector.Normalize();
            XYZ seed = Math.Abs(direction.DotProduct(XYZ.BasisZ)) < 0.9 ? XYZ.BasisZ : XYZ.BasisX;
            XYZ axisX = direction.CrossProduct(seed).Normalize();
            XYZ axisY = direction.CrossProduct(axisX).Normalize();
            var profile = new CurveLoop();
            profile.Append(Arc.Create(start, radiusFeet, 0.0, Math.PI, axisX, axisY));
            profile.Append(Arc.Create(start, radiusFeet, Math.PI, 2.0 * Math.PI, axisX, axisY));
            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { profile }, direction, length, options);
        }

        private static void ValidatePrimitive(IDictionary<string, object> values, string kind)
        {
            switch (kind.Trim().ToLowerInvariant())
            {
                case "box":
                case "cuboid":
                    XYZ min = PlanValues.Point(values, "min");
                    XYZ max = PlanValues.Point(values, "max");
                    if (max.X <= min.X || max.Y <= min.Y || max.Z <= min.Z)
                    {
                        throw new BridgeCommandException("box.max 必须在 min 的三个方向都更大。");
                    }
                    return;
                case "cylinder":
                case "tube":
                    XYZ start = PlanValues.Point(values, "start");
                    XYZ end = PlanValues.Point(values, "end");
                    if (start.DistanceTo(end) < 1e-7)
                    {
                        throw new BridgeCommandException("cylinder.start 与 end 不能重合。");
                    }
                    if (PlanValues.RequireMillimeters(values, "diameter_mm", "diameter") <= 0.0)
                    {
                        throw new BridgeCommandException("cylinder.diameter_mm 必须大于 0。");
                    }
                    return;
                case "extrusion":
                    List<Dictionary<string, object>> profile = PlanValues.DictionaryList(
                        PlanValues.Get(values, "profile"),
                        "extrusion.profile");
                    if (profile.Count < 3)
                    {
                        throw new BridgeCommandException("extrusion.profile 至少需要 3 个点。");
                    }
                    ReadDirection(values);
                    if (PlanValues.RequireMillimeters(values, "length_mm", "length") <= 0.0)
                    {
                        throw new BridgeCommandException("extrusion.length_mm 必须大于 0。");
                    }
                    return;
                default:
                    throw new BridgeCommandException("不支持几何原语 kind=“" + kind + "”。支持 box、cylinder、extrusion。");
            }
        }

        private static XYZ PointFromDictionary(IDictionary<string, object> values, string fieldName)
        {
            double x = PlanValues.RequireMillimeters(values, "x", "x_mm");
            double y = PlanValues.RequireMillimeters(values, "y", "y_mm");
            double z = PlanValues.Millimeters(values, 0.0, "z", "z_mm");
            return new XYZ(PlanValues.ToFeet(x), PlanValues.ToFeet(y), PlanValues.ToFeet(z));
        }

        private static XYZ ReadDirection(IDictionary<string, object> values)
        {
            Dictionary<string, object> direction = PlanValues.Dictionary(PlanValues.Get(values, "direction"), "extrusion.direction");
            XYZ vector = new XYZ(
                PlanValues.Number(direction, 0.0, "x"),
                PlanValues.Number(direction, 0.0, "y"),
                PlanValues.Number(direction, 0.0, "z"));
            if (vector.GetLength() < 1e-7)
            {
                throw new BridgeCommandException("extrusion.direction 不能为零向量。");
            }
            return vector.Normalize();
        }
    }
}
