using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace RevitCommandBridge
{
    /// <summary>
    /// create_swept_shape 的截面轮廓工厂：矩形 / 圆形 / 马蹄形及其环版。
    /// 输入为 mm 规格 + 放样路径起点的局部坐标系，输出可直接交给
    /// GeometryCreationUtilities.CreateSweptGeometry 的 CurveLoop 列表。
    /// </summary>
    internal static class RevitSectionFactory
    {
        public static CurveLoop BuildPath(List<XYZ> points, string fieldName)
        {
            if (points == null || points.Count < 2)
            {
                throw new BridgeCommandException(fieldName + " 至少需要 2 个点。");
            }
            var loop = new CurveLoop();
            for (int index = 0; index < points.Count - 1; index++)
            {
                XYZ start = points[index];
                XYZ end = points[index + 1];
                if (start.DistanceTo(end) < 1e-8)
                {
                    throw new BridgeCommandException(fieldName + " 不能包含重合的相邻点。");
                }
                loop.Append(Line.CreateBound(start, end));
            }
            return loop;
        }

        public static IList<CurveLoop> CreateSectionLoops(
            string shape,
            double widthMm,
            double heightMm,
            double wallThicknessMm,
            XYZ origin,
            XYZ tangent)
        {
            if (widthMm <= 0.0 || heightMm <= 0.0)
            {
                throw new BridgeCommandException("截面 width_mm / height_mm 必须大于 0。");
            }
            XYZ normal = tangent.Normalize();
            XYZ axisV = normal.Cross(XYZ.BasisZ);
            if (axisV.GetLength() < 1e-9)
            {
                axisV = normal.Cross(XYZ.BasisX);
            }
            axisV = axisV.Normalize();
            XYZ axisU = axisV.Cross(normal).Normalize();

            string normalized = (shape ?? "rect").Trim().ToLowerInvariant();
            var loops = new List<CurveLoop>();
            switch (normalized)
            {
                case "rect":
                case "rectangle":
                    loops.Add(RectLoop(origin, axisU, axisV, widthMm, heightMm, 0.0, false));
                    break;
                case "rect_ring":
                case "rectangle_ring":
                    RequireRing(wallThicknessMm, widthMm, heightMm);
                    loops.Add(RectLoop(origin, axisU, axisV, widthMm, heightMm, 0.0, false));
                    loops.Add(RectLoop(origin, axisU, axisV, widthMm, heightMm, wallThicknessMm, true));
                    break;
                case "circle":
                case "circular":
                    loops.Add(CircleLoop(origin, axisU, axisV, widthMm / 2.0, false));
                    break;
                case "circle_ring":
                case "circular_ring":
                    RequireRing(wallThicknessMm, widthMm, widthMm);
                    loops.Add(CircleLoop(origin, axisU, axisV, widthMm / 2.0, false));
                    loops.Add(CircleLoop(origin, axisU, axisV, widthMm / 2.0 - wallThicknessMm, true));
                    break;
                case "horseshoe":
                    if (heightMm <= widthMm / 2.0)
                    {
                        throw new BridgeCommandException("马蹄形 height_mm 必须大于 width_mm / 2。");
                    }
                    loops.Add(HorseshoeLoop(origin, axisU, axisV, widthMm, heightMm));
                    break;
                default:
                    throw new BridgeCommandException(
                        "create_swept_shape.section.shape 仅支持 rect、rect_ring、circle、circle_ring、horseshoe。");
            }
            return loops;
        }

        private static XYZ Map(XYZ origin, XYZ axisU, XYZ axisV, double xMm, double yMm)
        {
            return origin
                .Add(axisU.Multiply(PlanValues.ToFeet(xMm)))
                .Add(axisV.Multiply(PlanValues.ToFeet(yMm)));
        }

        private static void RequireRing(double wallThicknessMm, double outerWidth, double outerHeight)
        {
            if (wallThicknessMm <= 0.0)
            {
                throw new BridgeCommandException("环形截面需要 wall_thickness_mm 大于 0。");
            }
            if (wallThicknessMm * 2.0 >= Math.Min(outerWidth, outerHeight))
            {
                throw new BridgeCommandException("wall_thickness_mm 过大：内环不存在。");
            }
        }

        private static CurveLoop RectLoop(
            XYZ origin, XYZ axisU, XYZ axisV,
            double widthMm, double heightMm, double insetMm, bool reverse)
        {
            double halfWidth = widthMm / 2.0 - insetMm;
            double halfHeight = heightMm / 2.0 - insetMm;
            var corners = new List<XYZ>
            {
                Map(origin, axisU, axisV, -halfWidth, -halfHeight),
                Map(origin, axisU, axisV, halfWidth, -halfHeight),
                Map(origin, axisU, axisV, halfWidth, halfHeight),
                Map(origin, axisU, axisV, -halfWidth, halfHeight)
            };
            if (reverse)
            {
                corners.Reverse();
            }
            var loop = new CurveLoop();
            for (int index = 0; index < corners.Count; index++)
            {
                loop.Append(Line.CreateBound(corners[index], corners[(index + 1) % corners.Count]));
            }
            return loop;
        }

        private static CurveLoop CircleLoop(XYZ origin, XYZ axisU, XYZ axisV, double radiusMm, bool reverse)
        {
            double radius = PlanValues.ToFeet(radiusMm);
            XYZ center = origin;
            XYZ pointA = center.Add(axisU.Multiply(radius));
            XYZ pointB = center.Add(axisV.Multiply(radius));
            XYZ pointC = center.Subtract(axisU.Multiply(radius));
            XYZ pointD = center.Subtract(axisV.Multiply(radius));
            var loop = new CurveLoop();
            if (reverse)
            {
                loop.Append(Arc.Create(pointA, pointC, pointD));
                loop.Append(Arc.Create(pointC, pointA, pointB));
            }
            else
            {
                loop.Append(Arc.Create(pointA, pointC, pointB));
                loop.Append(Arc.Create(pointC, pointA, pointD));
            }
            return loop;
        }

        private static CurveLoop HorseshoeLoop(
            XYZ origin, XYZ axisU, XYZ axisV, double widthMm, double heightMm)
        {
            double radius = widthMm / 2.0;
            double straightHeight = heightMm - radius;
            XYZ a = Map(origin, axisU, axisV, -radius, 0);
            XYZ b = Map(origin, axisU, axisV, radius, 0);
            XYZ c = Map(origin, axisU, axisV, radius, straightHeight);
            XYZ d = Map(origin, axisU, axisV, -radius, straightHeight);
            XYZ top = Map(origin, axisU, axisV, 0, heightMm);
            var loop = new CurveLoop();
            loop.Append(Line.CreateBound(a, b));
            loop.Append(Line.CreateBound(b, c));
            loop.Append(Arc.Create(c, d, top));
            loop.Append(Line.CreateBound(d, a));
            return loop;
        }
    }
}
