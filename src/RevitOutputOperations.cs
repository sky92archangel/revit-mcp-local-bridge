using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitCommandBridge
{
    /// <summary>
    /// 常用出图和注释操作。所有入口都只接受结构化参数，避免向 Revit 暴露任意代码执行。
    /// </summary>
    internal static class RevitOutputOperations
    {
        /// <summary>
        /// 创建制图视图。
        /// Create a drafting view.
        /// </summary>
        public static Dictionary<string, object> CreateDraftingView(PlanStep step, PlanExecutionContext context)
        {
            ViewFamilyType type = ResolveViewFamilyType(context.Document, step.Arguments, ViewFamily.Drafting);
            string name = PlanValues.String(step.Arguments, null, "name", "view_name");
            var data = new Dictionary<string, object>
            {
                { "kind", "drafting" },
                { "type", type.Name },
                { "type_id", type.Id.GetValue() },
                { "name", name }
            };
            if (context.Preview)
            {
                return data;
            }

            ViewDrafting view = ViewDrafting.Create(context.Document, type.Id);
            SetOptionalViewName(view, name);
            data["element_id"] = view.Id.GetValue();
            data["element_ids"] = new[] { view.Id.GetValue() };
            data["name"] = view.Name;
            return data;
        }

        /// <summary>
        /// 创建剖面视图或详图视图。
        /// Create a section or detail view.
        /// </summary>
        public static Dictionary<string, object> CreateSectionView(PlanStep step, PlanExecutionContext context)
        {
            string kind = PlanValues.String(step.Arguments, "section", "kind", "view_kind")
                .Trim().ToLowerInvariant();
            ViewFamily family = kind == "detail" ? ViewFamily.Detail : ViewFamily.Section;
            if (kind != "section" && kind != "detail")
            {
                throw new BridgeCommandException("create_section_view.kind 仅支持 section 或 detail。");
            }

            ViewFamilyType type = ResolveViewFamilyType(context.Document, step.Arguments, family);
            XYZ origin = PlanValues.Point(step.Arguments, "origin");
            XYZ direction = ReadVector(step.Arguments, "direction", new XYZ(0.0, -1.0, 0.0));
            XYZ up = ReadVector(step.Arguments, "up", XYZ.BasisZ);
            // 正交化方向向量和上向量，构造右手坐标系
            // Orthonormalize direction and up vectors to build a right-handed coordinate system
            direction = direction.Normalize();
            up = (up - direction.Multiply(up.DotProduct(direction))).Normalize();
            if (up.GetLength() < 1e-8)
            {
                throw new BridgeCommandException("create_section_view.up 必须与 direction 不平行。");
            }
            XYZ right = direction.CrossProduct(up).Normalize();
            double widthMm = PlanValues.Millimeters(step.Arguments, 3000.0, "width_mm", "width");
            double heightMm = PlanValues.Millimeters(step.Arguments, 3000.0, "height_mm", "height");
            double depthMm = PlanValues.Millimeters(step.Arguments, 3000.0, "depth_mm", "depth");
            if (widthMm <= 0.0 || heightMm <= 0.0 || depthMm <= 0.0)
            {
                throw new BridgeCommandException("create_section_view 的 width_mm、height_mm、depth_mm 必须大于 0。");
            }
            string name = PlanValues.String(step.Arguments, null, "name", "view_name");
            var data = new Dictionary<string, object>
            {
                { "kind", kind },
                { "type", type.Name },
                { "type_id", type.Id.GetValue() },
                { "origin", PlanValues.PointData(origin) },
                { "direction", VectorData(direction) },
                { "up", VectorData(up) },
                { "width_mm", widthMm },
                { "height_mm", heightMm },
                { "depth_mm", depthMm },
                { "name", name }
            };
            if (context.Preview)
            {
                return data;
            }

            // 构造剖切包围盒：先定义局部坐标范围，再通过 Transform 放置到世界坐标
            // Build section bounding box: local extents first, then transform into world space
            var box = new BoundingBoxXYZ
            {
                Transform = Transform.Identity,
                Min = new XYZ(PlanValues.ToFeet(-widthMm / 2.0), PlanValues.ToFeet(-heightMm / 2.0), 0.0),
                Max = new XYZ(PlanValues.ToFeet(widthMm / 2.0), PlanValues.ToFeet(heightMm / 2.0), PlanValues.ToFeet(depthMm))
            };
            box.Transform.Origin = origin;
            box.Transform.BasisX = right;
            box.Transform.BasisY = up;
            box.Transform.BasisZ = direction;
            // 根据 kind 选择创建详图剖面或普通剖面
            // Choose between detail section and regular section based on kind
            ViewSection view = kind == "detail"
                ? ViewSection.CreateDetail(context.Document, type.Id, box)
                : ViewSection.CreateSection(context.Document, type.Id, box);
            SetOptionalViewName(view, name);
            data["element_id"] = view.Id.GetValue();
            data["element_ids"] = new[] { view.Id.GetValue() };
            data["name"] = view.Name;
            return data;
        }

        /// <summary>
        /// 创建立面视图。
        /// Create an elevation view.
        /// </summary>
        public static Dictionary<string, object> CreateElevationView(PlanStep step, PlanExecutionContext context)
        {
            ViewFamilyType type = ResolveViewFamilyType(context.Document, step.Arguments, ViewFamily.Elevation);
            View planView = ResolveView(context, step.Arguments, true, "plan_view_id", "plan_view", "view_id", "view");
            if (planView == null)
            {
                throw new BridgeCommandException("create_elevation_view 需要有效的平面视图 plan_view_id。");
            }
            XYZ origin = PlanValues.Point(step.Arguments, "origin");
            int index = PlanValues.Integer(step.Arguments, 0, "index", "direction_index");
            int scale = PlanValues.Integer(step.Arguments, 100, "scale");
            if (index < 0 || index > 3)
            {
                throw new BridgeCommandException("create_elevation_view.index 必须在 0 到 3 之间。");
            }
            if (scale <= 0)
            {
                throw new BridgeCommandException("create_elevation_view.scale 必须大于 0。");
            }
            string name = PlanValues.String(step.Arguments, null, "name", "view_name");
            var data = new Dictionary<string, object>
            {
                { "type", type.Name },
                { "type_id", type.Id.GetValue() },
                { "plan_view_id", planView.Id.GetValue() },
                { "origin", PlanValues.PointData(origin) },
                { "index", index },
                { "scale", scale },
                { "name", name }
            };
            if (context.Preview)
            {
                return data;
            }

            ElevationMarker marker = ElevationMarker.CreateElevationMarker(context.Document, type.Id, origin, scale);
            ViewSection view = marker.CreateElevation(context.Document, planView.Id, index);
            SetOptionalViewName(view, name);
            data["marker_id"] = marker.Id.GetValue();
            data["element_id"] = view.Id.GetValue();
            data["element_ids"] = new[] { view.Id.GetValue() };
            data["name"] = view.Name;
            return data;
        }

        /// <summary>
        /// 创建详图索引（大样图）。
        /// Create a callout view.
        /// </summary>
        public static Dictionary<string, object> CreateCallout(PlanStep step, PlanExecutionContext context)
        {
            View parent = ResolveView(context, step.Arguments, true, "parent_view_id", "parent_view", "view_id", "view");
            ViewFamilyType type = ResolveViewFamilyType(context.Document, step.Arguments, ViewFamily.Detail);
            XYZ start = PlanValues.Point(step.Arguments, "start");
            XYZ end = PlanValues.Point(step.Arguments, "end");
            if (start.DistanceTo(end) < 1e-8)
            {
                throw new BridgeCommandException("create_callout.start 与 end 不能重合。");
            }
            string name = PlanValues.String(step.Arguments, null, "name", "view_name");
            var data = new Dictionary<string, object>
            {
                { "parent_view_id", parent == null ? (object)null : parent.Id.GetValue() },
                { "type", type.Name },
                { "type_id", type.Id.GetValue() },
                { "start", PlanValues.PointData(start) },
                { "end", PlanValues.PointData(end) },
                { "name", name }
            };
            if (parent == null || context.Preview)
            {
                return data;
            }
            View created = ViewSection.CreateCallout(context.Document, parent.Id, type.Id, start, end);
            SetOptionalViewName(created, name);
            data["element_id"] = created.Id.GetValue();
            data["element_ids"] = new[] { created.Id.GetValue() };
            data["name"] = created.Name;
            return data;
        }

        /// <summary>
        /// 复制视图（可指定复制方式选项）。
        /// Duplicate a view with the specified duplicate option.
        /// </summary>
        public static Dictionary<string, object> DuplicateView(PlanStep step, PlanExecutionContext context)
        {
            View source = ResolveView(context, step.Arguments, true, "view_id", "view", "source_view_id", "source_view");
            string optionText = PlanValues.String(step.Arguments, "with_detailing", "option", "duplicate_option")
                .Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
            // 将字符串选项映射为 Revit 复制枚举
            // Map string option to Revit ViewDuplicateOption enum
            ViewDuplicateOption option;
            switch (optionText)
            {
                case "duplicate":
                case "as_duplicate":
                case "without_detailing":
                    option = ViewDuplicateOption.Duplicate; break;
                case "dependent":
                case "as_dependent": option = ViewDuplicateOption.AsDependent; break;
                case "with_detailing":
                case "detail": option = ViewDuplicateOption.WithDetailing; break;
                default: throw new BridgeCommandException("duplicate_view.option 仅支持 duplicate、as_duplicate、without_detailing、as_dependent、with_detailing。");
            }
            string name = PlanValues.String(step.Arguments, null, "name", "view_name");
            object rawTemplateId = PlanValues.Get(step.Arguments, "view_template_id", "template_id");
            string templateName = PlanValues.String(step.Arguments, null, "view_template", "template");
            var data = new Dictionary<string, object>
            {
                { "source_view_id", source == null ? (object)null : source.Id.GetValue() },
                { "option", option.ToString() },
                { "name", name },
                { "view_template", string.IsNullOrWhiteSpace(templateName) ? (object)null : templateName }
            };
            if (rawTemplateId != null)
            {
                data["view_template_id"] = rawTemplateId;
            }
            if (source == null || context.Preview)
            {
                return data;
            }
            if (!source.CanViewBeDuplicated(option))
            {
                throw new BridgeCommandException("当前视图不支持该复制方式：" + option);
            }
            ElementId id = source.Duplicate(option);
            View duplicate = context.Document.GetElement(id) as View;
            if (duplicate == null)
            {
                throw new BridgeCommandException("Revit 未返回复制后的视图。");
            }
            SetOptionalViewName(duplicate, name);
            ApplyOptionalViewTemplate(context, duplicate, rawTemplateId, templateName);
            data["element_id"] = duplicate.Id.GetValue();
            data["element_ids"] = new[] { duplicate.Id.GetValue() };
            data["name"] = duplicate.Name;
            return data;
        }

        /// <summary>
        /// 从源视图创建视图样板。
        /// Create a view template from a source view.
        /// </summary>
        public static Dictionary<string, object> CreateViewTemplate(PlanStep step, PlanExecutionContext context)
        {
            View source = ResolveView(context, step.Arguments, true, "view_id", "view", "source_view_id", "source_view");
            string name = PlanValues.String(step.Arguments, null, "name", "template_name");
            var data = new Dictionary<string, object>
            {
                { "source_view_id", source == null ? (object)null : source.Id.GetValue() },
                { "name", name }
            };
            if (source == null || context.Preview)
            {
                return data;
            }
            if (source.IsTemplate)
            {
                throw new BridgeCommandException("create_view_template.source_view 不能是已有视图样板。");
            }
            View template = source.CreateViewTemplate();
            if (template == null)
            {
                throw new BridgeCommandException("当前视图类型不支持创建视图样板。");
            }
            SetOptionalViewName(template, name);
            data["element_id"] = template.Id.GetValue();
            data["element_ids"] = new[] { template.Id.GetValue() };
            data["name"] = template.Name;
            return data;
        }

        /// <summary>
        /// 在视图中创建详图线。
        /// Create a detail curve in the specified view.
        /// </summary>
        public static Dictionary<string, object> CreateDetailCurve(PlanStep step, PlanExecutionContext context)
        {
            View view = ResolveView(context, step.Arguments, true, "view_id", "view");
            XYZ start = PlanValues.Point(step.Arguments, "start");
            XYZ end = PlanValues.Point(step.Arguments, "end");
            if (start.DistanceTo(end) < 1e-8)
            {
                throw new BridgeCommandException("create_detail_curve.start 与 end 不能重合。");
            }
            var data = new Dictionary<string, object>
            {
                { "view_id", view == null ? (object)null : view.Id.GetValue() },
                { "start", PlanValues.PointData(start) },
                { "end", PlanValues.PointData(end) }
            };
            if (view == null || context.Preview)
            {
                return data;
            }
            DetailCurve curve = context.Document.Create.NewDetailCurve(view, Line.CreateBound(start, end));
            data["element_id"] = curve.Id.GetValue();
            data["element_ids"] = new[] { curve.Id.GetValue() };
            return data;
        }

        /// <summary>
        /// 在视图中创建文字注释。
        /// Create a text note in the specified view.
        /// </summary>
        public static Dictionary<string, object> CreateTextNote(PlanStep step, PlanExecutionContext context)
        {
            View view = ResolveView(context, step.Arguments, true, "view_id", "view");
            XYZ point = PlanValues.Point(step.Arguments, "point");
            string text = PlanValues.String(step.Arguments, null, "text", "content");
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new BridgeCommandException("create_text_note.text 不能为空。");
            }
            TextNoteType type = ResolveTextNoteType(context.Document, step.Arguments);
            var data = new Dictionary<string, object>
            {
                { "view_id", view == null ? (object)null : view.Id.GetValue() },
                { "type_id", type.Id.GetValue() },
                { "type", type.Name },
                { "point", PlanValues.PointData(point) },
                { "text", text }
            };
            if (view == null || context.Preview)
            {
                return data;
            }
            TextNote note = TextNote.Create(context.Document, view.Id, point, text, type.Id);
            data["element_id"] = note.Id.GetValue();
            data["element_ids"] = new[] { note.Id.GetValue() };
            return data;
        }

        /// <summary>
        /// 创建尺寸标注。
        /// Create a dimension with reference elements.
        /// </summary>
        public static Dictionary<string, object> CreateDimension(PlanStep step, PlanExecutionContext context)
        {
            View view = ResolveView(context, step.Arguments, true, "view_id", "view");
            XYZ start = PlanValues.Point(step.Arguments, "start");
            XYZ end = PlanValues.Point(step.Arguments, "end");
            List<string> stableReferences = ReadStringList(step.Arguments, "references", "stable_references");
            if (stableReferences.Count < 2)
            {
                throw new BridgeCommandException("create_dimension.references 至少需要两个稳定引用字符串。");
            }
            var data = new Dictionary<string, object>
            {
                { "view_id", view == null ? (object)null : view.Id.GetValue() },
                { "start", PlanValues.PointData(start) },
                { "end", PlanValues.PointData(end) },
                { "reference_count", stableReferences.Count }
            };
            if (view == null || context.Preview)
            {
                return data;
            }
            var references = new ReferenceArray();
            foreach (string stable in stableReferences)
            {
                Reference reference;
                try
                {
                    reference = Reference.ParseFromStableRepresentation(context.Document, stable);
                }
                catch (Exception ex)
                {
                    throw new BridgeCommandException("无效稳定引用“" + stable + "”：" + ex.Message);
                }
                if (reference == null)
                {
                    throw new BridgeCommandException("无法解析稳定引用：“" + stable + "”。");
                }
                references.Append(reference);
            }
            Dimension dimension = context.Document.Create.NewDimension(view, Line.CreateBound(start, end), references);
            data["element_id"] = dimension.Id.GetValue();
            data["element_ids"] = new[] { dimension.Id.GetValue() };
            return data;
        }

        /// <summary>
        /// 创建独立标记（标签）。
        /// Create an independent tag on a referenced element.
        /// </summary>
        public static Dictionary<string, object> CreateTag(PlanStep step, PlanExecutionContext context)
        {
            View view = ResolveView(context, step.Arguments, true, "view_id", "view");
            string stable = PlanValues.String(step.Arguments, null, "reference", "stable_reference");
            ElementId tagTypeId = ResolveOptionalElementId(step.Arguments, "tag_type_id", "type_id");
            XYZ point = PlanValues.Point(step.Arguments, "point");
            bool leader = PlanValues.Boolean(step.Arguments, false, "leader", "has_leader");
            TagOrientation orientation = ParseEnum(step.Arguments, "orientation", TagOrientation.Horizontal);
            TagMode mode = ParseEnum(step.Arguments, "mode", TagMode.TM_ADDBY_CATEGORY);
            var data = new Dictionary<string, object>
            {
                { "view_id", view == null ? (object)null : view.Id.GetValue() },
                { "tag_type_id", tagTypeId == null ? (object)null : tagTypeId.GetValue() },
                { "point", PlanValues.PointData(point) },
                { "leader", leader },
                { "orientation", orientation.ToString() },
                { "mode", mode.ToString() }
            };
            if (string.IsNullOrWhiteSpace(stable))
            {
                throw new BridgeCommandException("create_tag.reference 不能为空。");
            }
            if (view == null || context.Preview)
            {
                return data;
            }
            Reference reference = Reference.ParseFromStableRepresentation(context.Document, stable);
            if (reference == null)
            {
                throw new BridgeCommandException("无法解析 create_tag.reference。");
            }
            IndependentTag tag = tagTypeId == null
                ? IndependentTag.Create(context.Document, view.Id, reference, leader, mode, orientation, point)
                : IndependentTag.Create(context.Document, tagTypeId, view.Id, reference, leader, orientation, point);
            data["element_id"] = tag.Id.GetValue();
            data["element_ids"] = new[] { tag.Id.GetValue() };
            return data;
        }

        /// <summary>
        /// 创建填充区域。
        /// Create a filled region from a closed boundary.
        /// </summary>
        public static Dictionary<string, object> CreateFilledRegion(PlanStep step, PlanExecutionContext context)
        {
            View view = ResolveView(context, step.Arguments, true, "view_id", "view");
            ElementId typeId = ResolvePositiveElementId(step.Arguments, "filled_region_type_id", "type_id");
            List<Dictionary<string, object>> points = PlanValues.DictionaryList(
                PlanValues.Get(step.Arguments, "boundary", "points"), "boundary");
            if (points.Count < 3)
            {
                throw new BridgeCommandException("create_filled_region.boundary 至少需要 3 个点。");
            }
            var curves = new List<Curve>();
            for (int index = 0; index < points.Count; index++)
            {
                XYZ a = ReadPointValue(points[index]);
                XYZ b = ReadPointValue(points[(index + 1) % points.Count]);
                curves.Add(Line.CreateBound(a, b));
            }
            var data = new Dictionary<string, object>
            {
                { "view_id", view == null ? (object)null : view.Id.GetValue() },
                { "filled_region_type_id", typeId.GetValue() },
                { "point_count", points.Count }
            };
            if (view == null || context.Preview)
            {
                return data;
            }
            var loops = new List<CurveLoop> { CurveLoop.Create(curves) };
            FilledRegion region = FilledRegion.Create(context.Document, typeId, view.Id, loops);
            data["element_id"] = region.Id.GetValue();
            data["element_ids"] = new[] { region.Id.GetValue() };
            return data;
        }

        /// <summary>
        /// 创建修订。
        /// Create a revision entry.
        /// </summary>
        public static Dictionary<string, object> CreateRevision(PlanStep step, PlanExecutionContext context)
        {
            string description = PlanValues.String(step.Arguments, null, "description", "name");
            string revisionDate = PlanValues.String(step.Arguments, null, "revision_date", "date");
            string issuedBy = PlanValues.String(step.Arguments, null, "issued_by");
            string issuedTo = PlanValues.String(step.Arguments, null, "issued_to");
            bool hasIssued = PlanValues.Get(step.Arguments, "issued") != null;
            bool issued = PlanValues.Boolean(step.Arguments, false, "issued");
            RevisionNumberType numberType = ParseEnum(step.Arguments, "number_type", RevisionNumberType.Numeric);
            RevisionVisibility visibility = ParseEnum(step.Arguments, "visibility", RevisionVisibility.CloudAndTagVisible);
            var data = new Dictionary<string, object>
            {
                { "description", description },
                { "revision_date", revisionDate },
                { "issued_by", issuedBy },
                { "issued_to", issuedTo },
                { "issued", hasIssued ? (object)issued : null },
                { "number_type", numberType.ToString() },
                { "visibility", visibility.ToString() }
            };
            if (context.Preview) return data;
            Revision revision = Revision.Create(context.Document);
            if (!string.IsNullOrWhiteSpace(description)) revision.Description = description;
            if (!string.IsNullOrWhiteSpace(revisionDate)) revision.RevisionDate = revisionDate;
            if (!string.IsNullOrWhiteSpace(issuedBy)) revision.IssuedBy = issuedBy;
            if (!string.IsNullOrWhiteSpace(issuedTo)) revision.IssuedTo = issuedTo;
            // revision.NumberType = numberType; // 在 Revit 2026 中已移除
            revision.Visibility = visibility;
            if (hasIssued) revision.Issued = issued;
            data["element_id"] = revision.Id.GetValue();
            data["element_ids"] = new[] { revision.Id.GetValue() };
            data["revision_number"] = revision.RevisionNumber;
            return data;
        }

        /// <summary>
        /// 创建修订云线。
        /// Create a revision cloud in the specified view.
        /// </summary>
        public static Dictionary<string, object> CreateRevisionCloud(PlanStep step, PlanExecutionContext context)
        {
            View view = ResolveView(context, step.Arguments, true, "view_id", "view");
            ElementId revisionId = context.ResolveSingleElementId(step.Arguments, "revision_id", "revision");
            List<Dictionary<string, object>> points = PlanValues.DictionaryList(
                PlanValues.Get(step.Arguments, "boundary", "points"), "boundary");
            if (points.Count < 3)
            {
                throw new BridgeCommandException("create_revision_cloud.boundary 至少需要 3 个点。");
            }
            var curves = new List<Curve>();
            for (int index = 0; index < points.Count; index++)
            {
                XYZ a = ReadPointValue(points[index]);
                XYZ b = ReadPointValue(points[(index + 1) % points.Count]);
                // 相邻点不能重合，否则 Revit 会抛出异常
                // Adjacent points must not coincide, or Revit will throw an error
                if (a.DistanceTo(b) < 1e-8)
                {
                    throw new BridgeCommandException("create_revision_cloud.boundary 不能存在相邻重合点。");
                }
                curves.Add(Line.CreateBound(a, b));
            }
            var data = new Dictionary<string, object>
            {
                { "view_id", view == null ? (object)null : view.Id.GetValue() },
                { "revision_id", revisionId.GetValue() == ElementId.InvalidElementId.GetValue() ? (object)null : revisionId.GetValue() },
                { "point_count", points.Count }
            };
            if (revisionId.GetValue() == ElementId.InvalidElementId.GetValue())
            {
                data["deferred"] = true;
                data["reason"] = "preview 中前置修订引用尚无真实 ID。";
                return data;
            }
            Revision revision = context.Document.GetElement(revisionId) as Revision;
            if (revision == null)
            {
                throw new BridgeCommandException("create_revision_cloud.revision_id 必须指向有效修订。");
            }
            if (view == null || context.Preview)
            {
                return data;
            }
            RevisionCloud cloud = RevisionCloud.Create(context.Document, view, revision.Id, curves);
            data["element_id"] = cloud.Id.GetValue();
            data["element_ids"] = new[] { cloud.Id.GetValue() };
            return data;
        }

        /// <summary>
        /// 创建明细表（常规、材料统计、关键字、视图列表、图纸列表、修订明细表）。
        /// Create a schedule (regular, material takeoff, key, view list, sheet list, or revision schedule).
        /// </summary>
        public static Dictionary<string, object> CreateSchedule(PlanStep step, PlanExecutionContext context)
        {
            string kind = PlanValues.String(step.Arguments, "regular", "kind", "schedule_kind")
                .Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
            ElementId categoryId = ElementId.InvalidElementId;
            bool categoryRequired = kind == "regular" || kind == "material_takeoff" || kind == "key" || kind == "key_schedule";
            if (categoryRequired)
            {
                categoryId = RevitLookups.ResolveCategoryId(context.Document, step.Arguments, BuiltInCategory.OST_GenericModel);
            }
            var data = new Dictionary<string, object>
            {
                { "kind", kind },
                { "category_id", categoryRequired ? (object)categoryId.GetValue() : null },
                { "name", PlanValues.String(step.Arguments, null, "name", "schedule_name") }
            };
            if (context.Preview)
            {
                return data;
            }

            ViewSchedule schedule;
            switch (kind)
            {
                case "regular": schedule = ViewSchedule.CreateSchedule(context.Document, categoryId); break;
                case "material_takeoff": schedule = ViewSchedule.CreateMaterialTakeoff(context.Document, categoryId); break;
                case "key":
                case "key_schedule": schedule = ViewSchedule.CreateKeySchedule(context.Document, categoryId); break;
                case "view_list": schedule = ViewSchedule.CreateViewList(context.Document); break;
                case "sheet_list": schedule = ViewSchedule.CreateSheetList(context.Document); break;
                case "revision":
                case "revision_schedule": schedule = ViewSchedule.CreateRevisionSchedule(context.Document); break;
                default: throw new BridgeCommandException("create_schedule.kind 仅支持 regular、material_takeoff、key、view_list、sheet_list、revision。");
            }

            string name = PlanValues.String(step.Arguments, null, "name", "schedule_name");
            SetOptionalViewName(schedule, name);
            ScheduleDefinition definition = schedule.Definition;
            if (PlanValues.Get(step.Arguments, "is_itemized") != null)
            {
                definition.IsItemized = PlanValues.Boolean(step.Arguments, true, "is_itemized");
            }
            if (PlanValues.Get(step.Arguments, "show_title") != null)
            {
                definition.ShowTitle = PlanValues.Boolean(step.Arguments, true, "show_title");
            }
            if (PlanValues.Get(step.Arguments, "show_headers") != null)
            {
                definition.ShowHeaders = PlanValues.Boolean(step.Arguments, true, "show_headers");
            }
            if (PlanValues.Get(step.Arguments, "show_grid_lines") != null)
            {
                definition.ShowGridLines = PlanValues.Boolean(step.Arguments, true, "show_grid_lines");
            }
            List<string> fields = ReadStringList(step.Arguments, "fields", "field_names");
            if (fields.Count > 0)
            {
                IList<SchedulableField> available = definition.GetSchedulableFields();
                foreach (string requested in fields)
                {
                // 先精确匹配，再降级为部分匹配
                // Try exact match first, then fall back to partial match
                SchedulableField field = available.FirstOrDefault(candidate =>
                    string.Equals(candidate.GetName(context.Document), requested, StringComparison.OrdinalIgnoreCase));
                if (field == null)
                {
                    field = available.FirstOrDefault(candidate =>
                        candidate.GetName(context.Document).IndexOf(requested, StringComparison.OrdinalIgnoreCase) >= 0);
                }
                    if (field == null)
                    {
                        throw new BridgeCommandException("找不到明细表字段：“" + requested + "”。");
                    }
                    ScheduleField added = definition.AddField(field);
                    data["last_field"] = added.GetName();
                }
            }
            data["element_id"] = schedule.Id.GetValue();
            data["element_ids"] = new[] { schedule.Id.GetValue() };
            data["name"] = schedule.Name;
            data["field_count"] = definition.GetFieldCount();
            return data;
        }

        /// <summary>
        /// 将明细表实例放置到图纸上。
        /// Place a schedule instance on a sheet.
        /// </summary>
        public static Dictionary<string, object> PlaceScheduleOnSheet(PlanStep step, PlanExecutionContext context)
        {
            ElementId sheetId = context.ResolveSingleElementId(step.Arguments, "sheet_id", "sheet", "target_sheet");
            ElementId scheduleId = context.ResolveSingleElementId(step.Arguments, "schedule_id", "schedule", "view_id", "view");
            XYZ point = PlanValues.Point(step.Arguments, "point");
            if (sheetId.GetValue() == ElementId.InvalidElementId.GetValue() ||
                scheduleId.GetValue() == ElementId.InvalidElementId.GetValue())
            {
                return new Dictionary<string, object>
                {
                    { "deferred", true },
                    { "point", PlanValues.PointData(point) },
                    { "reason", "preview 中前置图纸或明细表引用尚无真实 ID。" }
                };
            }
            ViewSheet sheet = context.Document.GetElement(sheetId) as ViewSheet;
            ViewSchedule schedule = context.Document.GetElement(scheduleId) as ViewSchedule;
            if (sheet == null || schedule == null)
            {
                throw new BridgeCommandException("place_schedule_on_sheet 需要有效的 sheet_id 和 schedule_id。");
            }
            var data = new Dictionary<string, object>
            {
                { "sheet_id", sheetId.GetValue() },
                { "schedule_id", scheduleId.GetValue() },
                { "point", PlanValues.PointData(point) }
            };
            if (context.Preview)
            {
                return data;
            }
            ScheduleSheetInstance instance = ScheduleSheetInstance.Create(context.Document, sheetId, scheduleId, point);
            data["element_id"] = instance.Id.GetValue();
            data["element_ids"] = new[] { instance.Id.GetValue() };
            return data;
        }

        /// <summary>
        /// 批量设置视图属性（比例、裁剪、样板、详细程度、规程、显示样式、名称等）。
        /// Set multiple view properties at once (scale, crop box, template, detail level, discipline, display style, name, etc.).
        /// </summary>
        public static Dictionary<string, object> SetViewProperties(PlanStep step, PlanExecutionContext context)
        {
            View view = ResolveView(context, step.Arguments, true, "view_id", "view");
            var changed = new List<string>();
            object scaleRaw = PlanValues.Get(step.Arguments, "scale");
            int scale = scaleRaw == null ? 0 : PlanValues.Integer(step.Arguments, 0, "scale");
            if (scaleRaw != null && scale <= 0)
            {
                throw new BridgeCommandException("set_view_properties.scale 必须大于 0。");
            }
            bool hasCrop = PlanValues.Get(step.Arguments, "crop_box", "crop") != null;
            BoundingBoxXYZ crop = hasCrop ? ReadBoundingBox(step.Arguments, "crop_box", "crop") : null;
            ElementId templateId = ResolveOptionalElementId(step.Arguments, "view_template_id", "template_id");
            bool clearTemplate = PlanValues.Boolean(step.Arguments, false, "clear_view_template", "clear_template");
            if (clearTemplate && templateId != null)
            {
                throw new BridgeCommandException("set_view_properties 不能同时传 view_template_id 和 clear_view_template=true。");
            }
            if (clearTemplate)
            {
                templateId = ElementId.InvalidElementId;
            }
            string name = PlanValues.String(step.Arguments, null, "name", "view_name");
            ViewDetailLevel detailLevel = ParseEnum(step.Arguments, "detail_level", ViewDetailLevel.Undefined);
            ViewDiscipline discipline = ParseEnum(step.Arguments, "discipline", ViewDiscipline.Architectural);
            DisplayStyle displayStyle = ParseEnum(step.Arguments, "display_style", DisplayStyle.Undefined);
            bool hasDetail = PlanValues.Get(step.Arguments, "detail_level") != null;
            bool hasDiscipline = PlanValues.Get(step.Arguments, "discipline") != null;
            bool hasDisplay = PlanValues.Get(step.Arguments, "display_style") != null;
            bool hasCropActive = PlanValues.Get(step.Arguments, "crop_active") != null;
            bool cropActive = PlanValues.Boolean(step.Arguments, false, "crop_active");
            bool hasCropVisible = PlanValues.Get(step.Arguments, "crop_visible") != null;
            bool cropVisible = PlanValues.Boolean(step.Arguments, false, "crop_visible");
            var data = new Dictionary<string, object>
            {
                { "view_id", view == null ? (object)null : view.Id.GetValue() },
                { "scale", scaleRaw == null ? (object)null : scale },
                { "name", name },
                { "changed", changed }
            };
            if (scaleRaw != null) changed.Add("scale");
            if (hasCrop) changed.Add("crop_box");
            if (templateId != null) changed.Add("view_template_id");
            if (!string.IsNullOrWhiteSpace(name)) changed.Add("name");
            if (hasDetail) changed.Add("detail_level");
            if (hasDiscipline) changed.Add("discipline");
            if (hasDisplay) changed.Add("display_style");
            if (hasCropActive) changed.Add("crop_active");
            if (hasCropVisible) changed.Add("crop_visible");
            if (view == null || context.Preview)
            {
                return data;
            }
            if (scaleRaw != null) view.Scale = scale;
            if (hasCrop) view.CropBox = crop;
            if (hasCropActive) view.CropBoxActive = cropActive;
            if (hasCropVisible) view.CropBoxVisible = cropVisible;
            if (templateId != null) view.ViewTemplateId = templateId;
            if (hasDetail) view.DetailLevel = detailLevel;
            if (hasDiscipline) view.Discipline = discipline;
            if (hasDisplay) view.DisplayStyle = displayStyle;
            SetOptionalViewName(view, name);
            data["name"] = view.Name;
            data["element_id"] = view.Id.GetValue();
            data["element_ids"] = new[] { view.Id.GetValue() };
            return data;
        }

        /// <summary>
        /// 设置指定元素的视图图形替换。
        /// Set graphic overrides for specified elements in a view.
        /// </summary>
        public static Dictionary<string, object> SetElementOverrides(PlanStep step, PlanExecutionContext context)
        {
            IList<ElementId> targets = context.ResolveElementIds(step.Arguments, "element_ids", "elements", "targets");
            if (targets.Count == 0)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
            }
            View view = ResolveGraphicsView(context, step.Arguments);
            OverrideGraphicSettings overrides = BuildOverrides(step.Arguments);
            var data = new Dictionary<string, object>
            {
                { "target_count", targets.Count },
                { "view_id", view == null ? (object)null : view.Id.GetValue() },
                { "view_name", view == null ? null : view.Name }
            };
            if (context.Preview || view == null)
            {
                return data;
            }
            foreach (ElementId id in targets)
            {
                view.SetElementOverrides(id, overrides);
            }
            data["element_ids"] = targets.Select(id => id.GetValue()).ToArray();
            return data;
        }

        /// <summary>
        /// 设置视图类别图形替换。
        /// Set graphic overrides for a category in a view.
        /// </summary>
        public static Dictionary<string, object> SetCategoryOverrides(PlanStep step, PlanExecutionContext context)
        {
            object rawCategory = PlanValues.Get(step.Arguments, "category", "category_id");
            if (rawCategory == null)
            {
                throw new BridgeCommandException("set_category_overrides 缺少 category。");
            }
            var lookup = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            lookup["category"] = rawCategory;
            ElementId categoryId = RevitLookups.ResolveCategoryId(
                context.Document, lookup, BuiltInCategory.OST_GenericModel);
            View view = ResolveGraphicsView(context, step.Arguments);
            OverrideGraphicSettings overrides = BuildOverrides(step.Arguments);
            var data = new Dictionary<string, object>
            {
                { "category", Convert.ToString(rawCategory, CultureInfo.InvariantCulture) },
                { "category_id", categoryId.GetValue() },
                { "view_id", view == null ? (object)null : view.Id.GetValue() },
                { "view_name", view == null ? null : view.Name }
            };
            if (context.Preview || view == null)
            {
                return data;
            }
            view.SetCategoryOverrides(categoryId, overrides);
            data["element_ids"] = new[] { categoryId.GetValue() };
            return data;
        }

        /// <summary>
        /// 管理视图过滤器（添加、移除、删除、清除）。
        /// Manage view filters (add, remove, delete, or clear).
        /// </summary>
        public static Dictionary<string, object> ManageViewFilters(PlanStep step, PlanExecutionContext context)
        {
            string action = PlanValues.String(step.Arguments, null, "action").Trim().ToLowerInvariant();
            switch (action)
            {
                case "add":
                    return AddViewFilter(step, context);
                case "remove":
                case "delete":
                case "clear":
                    return ModifyViewFilter(step, context, action);
                default:
                    throw new BridgeCommandException("manage_view_filters.action 仅支持 add、remove、delete、clear。");
            }
        }

        /// <summary>
        /// 添加视图过滤器（可附带类别、规则和图形替换）。
        /// Add a view filter with optional categories, rules, and graphic overrides.
        /// </summary>
        private static Dictionary<string, object> AddViewFilter(PlanStep step, PlanExecutionContext context)
        {
            string name = PlanValues.String(step.Arguments, null, "name", "filter_name");
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new BridgeCommandException("manage_view_filters.add 需要 name。");
            }
            List<string> categoryTokens = new List<string>();
            object rawCategories = PlanValues.Get(step.Arguments, "categories", "category");
            if (rawCategories != null)
            {
                foreach (object item in PlanValues.List(rawCategories, "categories"))
                {
                    string text = Convert.ToString(item, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        categoryTokens.Add(text.Trim());
                    }
                }
            }
            if (categoryTokens.Count == 0)
            {
                throw new BridgeCommandException("manage_view_filters.add 需要 categories 数组。");
            }
            var categoryIds = new List<ElementId>();
            foreach (string token in categoryTokens)
            {
                var lookup = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                lookup["category"] = token;
                categoryIds.Add(RevitLookups.ResolveCategoryId(
                    context.Document, lookup, BuiltInCategory.OST_GenericModel));
            }
            List<Dictionary<string, object>> rules = PlanValues.DictionaryList(
                PlanValues.Get(step.Arguments, "rules"), "manage_view_filters.rules");
            var data = new Dictionary<string, object>
            {
                { "action", "add" },
                { "name", name },
                { "categories", categoryTokens.ToArray() },
                { "rule_count", rules.Count }
            };
            View view = ResolveGraphicsView(context, step.Arguments);
            if (view == null)
            {
                throw new BridgeCommandException("manage_view_filters 需要 view_id（或存在活动视图）。");
            }
            data["view_id"] = view.Id.GetValue();
            data["view_name"] = view.Name;
            OverrideGraphicSettings overrides = BuildOverrides(step.Arguments);
            if (context.Preview)
            {
                return data;
            }

            // 按名称查找已有过滤器；不存在则新建（可选附带规则）
            // Look up existing filter by name; create new one if not found (with optional rules)
            ParameterFilterElement filter = new FilteredElementCollector(context.Document)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            bool created = filter == null;
            if (created)
            {
                ElementFilter elementFilter = rules.Count == 0
                    ? null
                    : BuildElementFilter(context.Document, categoryIds, rules);
                filter = elementFilter == null
                    ? ParameterFilterElement.Create(context.Document, name, categoryIds)
                    : ParameterFilterElement.Create(context.Document, name, categoryIds, elementFilter);
            }
            view.AddFilter(filter.Id);
            view.SetFilterVisibility(filter.Id, true);
            view.SetFilterOverrides(filter.Id, overrides);
            data["created"] = created;
            data["filter_id"] = filter.Id.GetValue();
            data["element_id"] = filter.Id.GetValue();
            data["element_ids"] = new[] { filter.Id.GetValue() };
            return data;
        }

        /// <summary>
        /// 修改视图过滤器（按 action 执行移除或删除操作）。
        /// Modify a view filter (remove or delete by action).
        /// </summary>
        private static Dictionary<string, object> ModifyViewFilter(
            PlanStep step, PlanExecutionContext context, string action)
        {
            View view = ResolveGraphicsView(context, step.Arguments);
            if (view == null)
            {
                throw new BridgeCommandException("manage_view_filters 需要 view_id（或存在活动视图）。");
            }
            var data = new Dictionary<string, object>
            {
                { "action", action },
                { "view_id", view.Id.GetValue() },
                { "view_name", view.Name }
            };
            if (context.Preview)
            {
                return data;
            }

            // clear 操作：移除视图上所有过滤器
            // Clear action: remove all filters from the view
            if (action == "clear")
            {
                List<ElementId> current = view.GetFilters().ToList();
                foreach (ElementId filterId in current)
                {
                    view.RemoveFilter(filterId);
                }
                data["removed_count"] = current.Count;
                return data;
            }

            ElementId targetId = ResolveFilterId(step, context, view);
            if (action == "remove")
            {
                view.RemoveFilter(targetId);
                data["removed_count"] = 1;
                return data;
            }
            ICollection<ElementId> deleted = context.Document.Delete(new List<ElementId> { targetId });
            data["deleted_count"] = deleted.Count;
            return data;
        }

        /// <summary>
        /// 解析过滤器 ID（按 filter_id 或 filter_name 查找）。
        /// Resolve a filter ElementId by filter_id or filter_name.
        /// </summary>
        private static ElementId ResolveFilterId(PlanStep step, PlanExecutionContext context, View view)
        {
            object rawId = PlanValues.Get(step.Arguments, "filter_id");
            if (rawId != null)
            {
                return new ElementId(RevitLookups.ParsePositiveId(rawId, "filter_id"));
            }
            string name = PlanValues.String(step.Arguments, null, "name", "filter_name");
            ParameterFilterElement filter = new FilteredElementCollector(context.Document)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (filter == null)
            {
                throw new BridgeCommandException("找不到视图过滤器：“" + name + "”。");
            }
            return filter.Id;
        }

        /// <summary>
        /// 根据规则构造 ElementParameterFilter（组合参数相等条件）。
        /// Build an ElementParameterFilter from rule definitions (parameter equals conditions).
        /// </summary>
        private static ElementFilter BuildElementFilter(
            Document document,
            ICollection<ElementId> categoryIds,
            List<Dictionary<string, object>> rules)
        {
            var filterRules = new List<FilterRule>();
            foreach (Dictionary<string, object> rule in rules)
            {
                string parameterName = PlanValues.String(rule, null, "parameter", "parameter_name");
                if (string.IsNullOrWhiteSpace(parameterName))
                {
                    throw new BridgeCommandException("rules[].parameter 不能为空。");
                }
                ElementId parameterId = FindParameterIdForCategories(document, categoryIds, parameterName);
                object equals = PlanValues.Get(rule, "equals", "value");
                if (equals == null)
                {
                    throw new BridgeCommandException("rules[].equals 不能为空。");
                }
                filterRules.Add(CreateEqualsRule(parameterId, equals, parameterName));
            }
            return new ElementParameterFilter(filterRules, false);
        }

        /// <summary>
        /// 创建参数相等过滤器规则（支持布尔、整数、浮点数、字符串类型的值）。
        /// Create a parameter equals filter rule supporting bool, int, double, and string value types.
        /// </summary>
        private static FilterRule CreateEqualsRule(ElementId parameterId, object value, string parameterName)
        {
            if (value is bool)
            {
                return ParameterFilterRuleFactory.CreateEqualsRule(parameterId, (bool)value ? 1 : 0);
            }
            if (value is int || value is short || value is byte || value is long)
            {
                return ParameterFilterRuleFactory.CreateEqualsRule(
                    parameterId, Convert.ToInt32(value, CultureInfo.InvariantCulture));
            }
            if (value is double || value is float || value is decimal)
            {
#if REVIT2022_OR_GREATER
return ParameterFilterRuleFactory.CreateEqualsRule(
                    parameterId, Convert.ToString(value, CultureInfo.InvariantCulture));
#else
                return new FilterStringRule(
                    new ParameterValueProvider(parameterId),
                    new FilterStringEquals(),
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    false);
#endif
            }
#if REVIT2022_OR_GREATER
            return ParameterFilterRuleFactory.CreateEqualsRule(
                parameterId, Convert.ToString(value, CultureInfo.InvariantCulture));
#else
            return new FilterStringRule(
                new ParameterValueProvider(parameterId),
                new FilterStringEquals(),
                Convert.ToString(value, CultureInfo.InvariantCulture),
                false);
#endif
        }

        /// <summary>
        /// 在给定类别中查找参数 ID（先查实例参数，再查类型参数）。
        /// Find a parameter ElementId across given categories (instance first, then type).
        /// </summary>
        private static ElementId FindParameterIdForCategories(
            Document document, ICollection<ElementId> categoryIds, string parameterName)
        {
            foreach (ElementId categoryId in categoryIds)
            {
                Element sample = new FilteredElementCollector(document)
                    .WherePasses(new ElementCategoryFilter(categoryId))
                    .WhereElementIsNotElementType()
                    .FirstOrDefault();
                Parameter parameter = sample == null ? null : sample.LookupParameter(parameterName);
                if (parameter != null)
                {
                    return parameter.Id;
                }
                Element sampleType = new FilteredElementCollector(document)
                    .WherePasses(new ElementCategoryFilter(categoryId))
                    .WhereElementIsElementType()
                    .FirstOrDefault();
                parameter = sampleType == null ? null : sampleType.LookupParameter(parameterName);
                if (parameter != null)
                {
                    return parameter.Id;
                }
            }
            throw new BridgeCommandException(
                "类别元素上找不到参数“" + parameterName + "”，无法构造过滤器规则。");
        }

        /// <summary>
        /// 设置平面视图的视图范围（顶部、剖切面、底部、视图深度）。
        /// Set the view range of a plan view (top, cut plane, bottom, view depth).
        /// </summary>
        public static Dictionary<string, object> SetViewRange(PlanStep step, PlanExecutionContext context)
        {
            ElementId viewId = context.ResolveSingleElementId(step.Arguments, "view_id", "view");
            if (viewId.GetValue() == ElementId.InvalidElementId.GetValue())
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置视图引用尚无真实 ID。" } };
            }
            ViewPlan viewPlan = context.Document.GetElement(viewId) as ViewPlan;
            if (viewPlan == null || viewPlan.IsTemplate)
            {
                throw new BridgeCommandException("set_view_range 的 view_id 必须指向平面视图。");
            }
            // 视图范围有四个槽位：顶部、剖切面、底部、视图深度，对应 PlanViewPlane 枚举值
            // Four view range slots: top, cut plane, bottom, view depth, mapped to PlanViewPlane enum values
            var slots = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                { "top", 0 },
                { "cut_plane", 1 },
                { "bottom", 2 },
                { "view_depth", 3 }
            };
            var changes = new List<string>();
            var slotSpecs = new List<KeyValuePair<int, Dictionary<string, object>>>();
            foreach (KeyValuePair<string, int> slot in slots)
            {
                object raw = PlanValues.Get(step.Arguments, slot.Key);
                if (raw == null)
                {
                    continue;
                }
                Dictionary<string, object> spec = PlanValues.Dictionary(raw, slot.Key);
                slotSpecs.Add(new KeyValuePair<int, Dictionary<string, object>>(slot.Value, spec));
                changes.Add(slot.Key);
            }
            if (slotSpecs.Count == 0)
            {
                throw new BridgeCommandException(
                    "set_view_range 至少提供一个槽位：top、cut_plane、bottom、view_depth（{level/level_id, offset_mm}）。");
            }
            var data = new Dictionary<string, object>
            {
                { "view_id", viewId.GetValue() },
                { "view_name", viewPlan.Name },
                { "changed", changes.ToArray() }
            };
            if (context.Preview)
            {
                return data;
            }

            PlanViewRange range = viewPlan.GetViewRange();
            foreach (KeyValuePair<int, Dictionary<string, object>> slot in slotSpecs)
            {
                object rawLevel = PlanValues.Get(slot.Value, "level", "level_id", "level_name");
                if (rawLevel != null)
                {
                    var lookup = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    if (PlanValues.Get(slot.Value, "level_id") != null)
                    {
                        lookup["level_id"] = PlanValues.Get(slot.Value, "level_id");
                    }
                    else
                    {
                        lookup["level"] = rawLevel;
                    }
range.SetLevelId((PlanViewPlane)slot.Key, RevitLookups.ResolveLevel(context.Document, lookup).Id);
                }
                object rawOffset = PlanValues.Get(slot.Value, "offset_mm", "offset");
                if (rawOffset != null)
                {
range.SetOffset((PlanViewPlane)slot.Key,
                        PlanValues.ToFeet(PlanValues.ParseMillimeters(rawOffset, "offset_mm")));
                }
            }
            viewPlan.SetViewRange(range);
            data["element_id"] = viewId.GetValue();
            data["element_ids"] = new[] { viewId.GetValue() };
            return data;
        }

        /// <summary>
        /// 管理明细表字段（添加、移除、隐藏、显示字段；添加过滤器、排序、设置逐项列举）。
        /// Manage schedule fields (add, remove, hide, show field; add filter, sort, set itemized).
        /// </summary>
        public static Dictionary<string, object> ManageScheduleFields(PlanStep step, PlanExecutionContext context)
        {
            string action = PlanValues.String(step.Arguments, null, "action").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(action))
            {
                throw new BridgeCommandException("manage_schedule_fields 缺少 action（add_field、remove_field、hide_field、show_field、add_filter、sort、set_itemized）。");
            }
            ElementId scheduleId = context.ResolveSingleElementId(step.Arguments, "schedule_id", "schedule", "target");
            if (scheduleId.GetValue() == ElementId.InvalidElementId.GetValue())
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置明细表引用尚无真实 ID。" } };
            }
            ViewSchedule schedule = context.Document.GetElement(scheduleId) as ViewSchedule;
            if (schedule == null || schedule.IsTemplate)
            {
                throw new BridgeCommandException("manage_schedule_fields 的 schedule_id 必须指向明细表。");
            }
            ScheduleDefinition definition = schedule.Definition;
            if (definition == null)
            {
                throw new BridgeCommandException("明细表没有可编辑的字段定义。");
            }
            string fieldName = PlanValues.String(step.Arguments, null, "field", "parameter", "parameter_name", "heading");
            var data = new Dictionary<string, object>
            {
                { "action", action },
                { "schedule_id", scheduleId.GetValue() },
                { "field", fieldName }
            };
            if (context.Preview)
            {
                return data;
            }

            switch (action)
            {
                case "add_field":
                {
                    if (string.IsNullOrWhiteSpace(fieldName))
                    {
                        throw new BridgeCommandException("add_field 需要 field（参数名）。");
                    }
                    bool isInstance = PlanValues.Boolean(step.Arguments, true, "is_instance", "instance");
                    ScheduleField addedField = AddScheduleField(context.Document, definition, fieldName, isInstance);
                    string heading = PlanValues.String(step.Arguments, null, "heading");
                    if (!string.IsNullOrWhiteSpace(heading))
                    {
                        addedField.ColumnHeading = heading;
                    }
                    data["field_id"] = addedField.FieldId;
                    break;
                }
                case "remove_field":
                case "hide_field":
                case "show_field":
                {
                    int index = FindScheduleFieldIndex(context.Document, definition, fieldName);
                    if (index < 0)
                    {
                        throw new BridgeCommandException("明细表中找不到字段\"" + fieldName + "\"。");
                    }
                    if (action == "remove_field")
                    {
                        definition.RemoveField(index);
                    }
                    else
                    {
                        definition.GetField(index).IsHidden = action == "hide_field";
                    }
                    break;
                }
                case "add_filter":
                {
                    if (string.IsNullOrWhiteSpace(fieldName))
                    {
                        throw new BridgeCommandException("add_filter 需要 field（参数名）。");
                    }
                    object equals = PlanValues.Get(step.Arguments, "equals", "value");
                    if (equals == null)
                    {
                        throw new BridgeCommandException("add_filter 需要 equals 值。");
                    }
                    int index = FindScheduleFieldIndex(context.Document, definition, fieldName);
                    if (index < 0)
                    {
                        ScheduleField added = AddScheduleField(
                            context.Document, definition, fieldName, true);
                        index = FindScheduleFieldIndex(context.Document, definition, fieldName);
                        if (index < 0)
                        {
                            throw new BridgeCommandException("未能定位新增字段：" + fieldName);
                        }
                    }
                    ScheduleField field = definition.GetField(index);
                    definition.AddFilter(BuildScheduleFilter(field, equals));
                    break;
                }
                case "sort":
                {
                    if (string.IsNullOrWhiteSpace(fieldName))
                    {
                        throw new BridgeCommandException("sort 需要 field（参数名）。");
                    }
                    int index = FindScheduleFieldIndex(context.Document, definition, fieldName);
                    if (index < 0)
                    {
                        throw new BridgeCommandException("明细表中找不到字段\"" + fieldName + "\"。");
                    }
                    definition.AddSortGroupField(new ScheduleSortGroupField(
                        definition.GetField(index).FieldId));
                    break;
                }
                case "set_itemized":
                    definition.IsItemized = PlanValues.Boolean(step.Arguments, true, "itemized");
                    break;
                default:
                    throw new BridgeCommandException(
                        "manage_schedule_fields.action 仅支持 add_field、remove_field、hide_field、show_field、add_filter、sort、set_itemized。");
            }
            data["field_count"] = definition.GetFieldCount();
            data["element_ids"] = new[] { scheduleId.GetValue() };
            return data;
        }

        /// <summary>
        /// 添加明细表字段（自动判断实例参数或类型参数）。
        /// Add a schedule field, automatically detecting instance vs. type parameter.
        /// </summary>
        private static ScheduleField AddScheduleField(
            Document document, ScheduleDefinition definition, string parameterName, bool isInstance)
        {
            // 先在实例上查找参数，找不到再转到类型参数
            // Look up parameter on instance elements first, then fall back to type parameters
            Element sample = definition.CategoryId == null
                ? null
                : new FilteredElementCollector(document)
                    .WherePasses(new ElementCategoryFilter(definition.CategoryId))
                    .WhereElementIsNotElementType()
                    .FirstOrDefault();
            Parameter parameter = sample == null ? null : sample.LookupParameter(parameterName);
            ScheduleFieldType fieldType;
            ElementId parameterId;
            if (parameter != null)
            {
                fieldType = ScheduleFieldType.Instance;
                parameterId = parameter.Id;
            }
            else
            {
                Element sampleType = definition.CategoryId == null
                    ? null
                    : new FilteredElementCollector(document)
                        .WherePasses(new ElementCategoryFilter(definition.CategoryId))
                        .WhereElementIsElementType()
                        .FirstOrDefault();
                Parameter typeParameter = sampleType == null ? null : sampleType.LookupParameter(parameterName);
                if (typeParameter == null)
                {
                    throw new BridgeCommandException(
                        "明细表类别元素上找不到参数“" + parameterName + "”。");
                }
                fieldType = ScheduleFieldType.ElementType;
                parameterId = typeParameter.Id;
            }
            return definition.AddField(fieldType, parameterId);
        }

        /// <summary>
        /// 按列标题或可调度字段名称查找明细表字段索引。
        /// Find the index of a schedule field by column heading or schedulable field name.
        /// </summary>
        private static int FindScheduleFieldIndex(Document document, ScheduleDefinition definition, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(fieldName))
            {
                return -1;
            }
            for (int index = 0; index < definition.GetFieldCount(); index++)
            {
                ScheduleField field = definition.GetField(index);
                if (string.Equals(field.ColumnHeading, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
                SchedulableField schedulable = field.GetSchedulableField();
                if (schedulable != null &&
                    string.Equals(schedulable.GetName(document), fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
            return -1;
        }

        /// <summary>
        /// 构造明细表字段相等过滤器（支持布尔、整数、浮点数、字符串类型）。
        /// Build a schedule filter with equals comparison, supporting multiple value types.
        /// </summary>
        private static ScheduleFilter BuildScheduleFilter(ScheduleField field, object value)
        {
            if (value is bool)
            {
                return new ScheduleFilter(field.FieldId, ScheduleFilterType.Equal, (bool)value ? 1 : 0);
            }
            if (value is int || value is short || value is byte || value is long)
            {
                return new ScheduleFilter(
                    field.FieldId, ScheduleFilterType.Equal, Convert.ToInt32(value, CultureInfo.InvariantCulture));
            }
            if (value is double || value is float || value is decimal)
            {
                return new ScheduleFilter(
                    field.FieldId, ScheduleFilterType.Equal, Convert.ToDouble(value, CultureInfo.InvariantCulture));
            }
            return new ScheduleFilter(
                field.FieldId, ScheduleFilterType.Equal, Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// 管理图形资源（线样式、填充图案）。
        /// Manage graphics resources (line styles, fill patterns).
        /// </summary>
        public static Dictionary<string, object> ManageGraphicsResources(PlanStep step, PlanExecutionContext context)
        {
            string action = PlanValues.String(step.Arguments, null, "action", "kind", "resource")
                .Trim().ToLowerInvariant();
            string name = PlanValues.String(step.Arguments, null, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new BridgeCommandException("manage_graphics_resources 需要 name。");
            }
            var data = new Dictionary<string, object>
            {
                { "action", action },
                { "name", name }
            };
            if (action != "line_style" && action != "fill_pattern")
            {
                throw new BridgeCommandException("manage_graphics_resources.action 仅支持 line_style、fill_pattern。");
            }
            if (context.Preview)
            {
                return data;
            }

            switch (action)
            {
                case "line_style":
                {
                    // 检查线样式是否已存在，不存在则新建子类别
                    // Check if line style already exists; create new subcategory if not
                    Category lineStyles = context.Document.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                    if (lineStyles == null)
                    {
                        throw new BridgeCommandException("当前项目没有线样式类别。");
                    }
                    Category existing = null;
                    foreach (Category sub in lineStyles.SubCategories)
                    {
                        if (string.Equals(sub.Name, name, StringComparison.OrdinalIgnoreCase))
                        {
                            existing = sub;
                            break;
                        }
                    }
                    if (existing != null)
                    {
                        data["created"] = false;
                        data["element_id"] = existing.Id.GetValue();
                    }
                    else
                    {
                        Category created = context.Document.Settings.Categories.NewSubcategory(lineStyles, name);
                        if (created == null)
                        {
                            throw new BridgeCommandException("创建线样式失败：" + name);
                        }
                        data["created"] = true;
                        data["element_id"] = created.Id.GetValue();
                    }
                    break;
                }
                case "fill_pattern":
                {
                    FillPatternElement existing = new FilteredElementCollector(context.Document)
                        .OfClass(typeof(FillPatternElement))
                        .Cast<FillPatternElement>()
                        .FirstOrDefault(candidate =>
                            string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (existing != null)
                    {
                        data["created"] = false;
                        data["element_id"] = existing.Id.GetValue();
                        break;
                    }
                    // 构造填充图案对象（区分模型/绘图方向和宿主方向）
                    // Build fill pattern object (model/drafting target with correct host orientation)
                    string target = PlanValues.String(step.Arguments, "drafting", "target").ToLowerInvariant();
                    FillPatternTarget fillTarget = target == "model"
                        ? FillPatternTarget.Model
                        : FillPatternTarget.Drafting;
                    FillPatternHostOrientation orientation = fillTarget == FillPatternTarget.Model
                        ? FillPatternHostOrientation.ToHost
                        : FillPatternHostOrientation.ToView;
                    FillPattern pattern = new FillPattern(name, fillTarget, orientation);
                    FillPatternElement created = FillPatternElement.Create(context.Document, pattern);
                    data["created"] = true;
                    data["element_id"] = created.Id.GetValue();
                    break;
                }
            }
            data["element_ids"] = new[] { (int)data["element_id"] };
            return data;
        }

        /// <summary>
        /// 解析图形视图（排除明细表、浏览器等非图形视图类型）。
        /// Resolve a graphics view, rejecting non-graphical view types (schedule, browser, etc.).
        /// </summary>
        private static View ResolveGraphicsView(PlanExecutionContext context, IDictionary<string, object> arguments)
        {
            View view = ResolveView(context, arguments, true, "view_id", "view");
            if (view == null)
            {
                return null;
            }
            // 图形替换不适用于明细表、浏览器等非图形视图
            // Graphic overrides are not valid for non-graphical view types like schedules and browsers
            if (view.ViewType == ViewType.Schedule || view.ViewType == ViewType.SystemBrowser ||
                view.ViewType == ViewType.ProjectBrowser || view.ViewType == ViewType.Undefined)
            {
                throw new BridgeCommandException("图形替换不适用于视图类型：" + view.ViewType);
            }
            return view;
        }

        /// <summary>
        /// 从参数字典构造图形替换设置（颜色、线宽、半色调、表面透明度、表面颜色）。
        /// Build OverrideGraphicSettings from arguments (line color, weight, halftone, surface transparency, surface color).
        /// </summary>
        private static OverrideGraphicSettings BuildOverrides(IDictionary<string, object> arguments)
        {
            var overrides = new OverrideGraphicSettings();
            object rawColor = PlanValues.Get(arguments, "color", "line_color", "projection_color");
            if (rawColor != null)
            {
                overrides.SetProjectionLineColor(ReadColor(rawColor, "color"));
            }
            object rawWeight = PlanValues.Get(arguments, "line_weight", "projection_line_weight");
            if (rawWeight != null)
            {
                int weight = PlanValues.Integer(arguments, 1, "line_weight", "projection_line_weight");
                if (weight < 1 || weight > 16)
                {
                    throw new BridgeCommandException("line_weight 必须在 1 到 16 之间。");
                }
                overrides.SetProjectionLineWeight(weight);
            }
            if (PlanValues.Boolean(arguments, false, "halftone"))
            {
                overrides.SetHalftone(true);
            }
            object rawTransparency = PlanValues.Get(arguments, "surface_transparency", "transparency");
            if (rawTransparency != null)
            {
                int transparency = PlanValues.Integer(arguments, 0, "surface_transparency", "transparency");
                if (transparency < 0 || transparency > 100)
                {
                    throw new BridgeCommandException("surface_transparency 必须在 0 到 100 之间。");
                }
                overrides.SetSurfaceTransparency(transparency);
            }
            object rawSurfaceColor = PlanValues.Get(arguments, "surface_color");
            if (rawSurfaceColor != null)
            {
                overrides.SetSurfaceForegroundPatternColor(ReadColor(rawSurfaceColor, "surface_color"));
            }
            return overrides;
        }

        /// <summary>
        /// 从字典解析 Color（R/G/B 值 0-255）。
        /// Parse a Color from a dictionary with r/g/b values (0-255).
        /// </summary>
        private static Color ReadColor(object raw, string fieldName)
        {
            Dictionary<string, object> values = PlanValues.Dictionary(raw, fieldName);
            int r = PlanValues.Integer(values, 0, "r", "red");
            int g = PlanValues.Integer(values, 0, "g", "green");
            int b = PlanValues.Integer(values, 0, "b", "blue");
            if (r < 0 || r > 255 || g < 0 || g > 255 || b < 0 || b > 255)
            {
                throw new BridgeCommandException(fieldName + " 的 r/g/b 必须在 0 到 255 之间。");
            }
            return new Color((byte)r, (byte)g, (byte)b);
        }

        /// <summary>
        /// 导出视图或明细表为图片、DWG、DXF、IFC、CSV 格式。
        /// Export views or schedules to image, DWG, DXF, IFC, or CSV format.
        /// </summary>
        public static Dictionary<string, object> Export(PlanStep step, PlanExecutionContext context)
        {
            string format = PlanValues.String(step.Arguments, null, "format", "kind", "export_kind");
            if (string.IsNullOrWhiteSpace(format))
            {
                throw new BridgeCommandException("export 缺少 format（image、dwg、ifc、schedule_csv）。");
            }
            format = format.Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
            string output = PlanValues.String(step.Arguments, null, "output_path", "path", "file");
            if (string.IsNullOrWhiteSpace(output))
            {
                throw new BridgeCommandException("export.output_path 不能为空。");
            }
            string fullOutput = Path.GetFullPath(output);
            var data = new Dictionary<string, object>
            {
                { "format", format },
                { "output_path", fullOutput }
            };
            if (format == "schedule_csv" || format == "schedule")
            {
                ElementId scheduleId = context.ResolveSingleElementId(step.Arguments, "schedule_id", "schedule", "view_id", "view");
                if (scheduleId.GetValue() == ElementId.InvalidElementId.GetValue())
                {
                    data["deferred"] = true;
                    return data;
                }
                ViewSchedule schedule = context.Document.GetElement(scheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    throw new BridgeCommandException("export schedule_csv 需要有效的 schedule_id。");
                }
                data["schedule_id"] = scheduleId.GetValue();
                if (context.Preview) return data;
                string folder = Path.GetDirectoryName(fullOutput);
                string fileName = Path.GetFileNameWithoutExtension(fullOutput);
                EnsureOutputDirectory(folder);
                var options = new ViewScheduleExportOptions();
                options.FieldDelimiter = PlanValues.String(step.Arguments, ",", "delimiter");
                options.Title = PlanValues.Boolean(step.Arguments, true, "title");
                schedule.Export(folder, fileName, options);
                data["exported"] = true;
                return data;
            }

            List<ElementId> viewIds = ResolveExportViewIds(step, context);
            data["view_ids"] = viewIds.Select(id => id.GetValue()).ToArray();
            if (context.Preview)
            {
                return data;
            }
            if (format == "image" || format == "png" || format == "jpg" || format == "jpeg")
            {
                EnsureOutputDirectory(Path.GetDirectoryName(fullOutput));
                // 配置图片导出选项：自动选择导出范围、缩放方式和分辨率
                // Configure image export options: auto-select export range, zoom mode, and resolution
                var options = new ImageExportOptions
                {
                    FilePath = fullOutput,
                    ExportRange = viewIds.Count == 0 ? ExportRange.CurrentView : ExportRange.SetOfViews,
                    HLRandWFViewsFileType = ParseImageFileType(format, step.Arguments),
                    ShadowViewsFileType = ParseImageFileType(format, step.Arguments),
                    ZoomType = ZoomFitType.FitToPage,
                    FitDirection = FitDirectionType.Horizontal,
                    ImageResolution = ImageResolution.DPI_150
                };
                if (viewIds.Count > 0) options.SetViewsAndSheets(viewIds);
                context.Document.ExportImage(options);
                data["exported"] = true;
                return data;
            }
            if (format == "dwg" || format == "dxf")
            {
                if (viewIds.Count == 0) throw new BridgeCommandException("export dwg/dxf 至少需要 view_ids，或显式传 active_view=true。");
                string folder = Path.GetDirectoryName(fullOutput);
                EnsureOutputDirectory(folder);
                string fileName = Path.GetFileNameWithoutExtension(fullOutput);
                if (format == "dwg")
                {
                    context.Document.Export(folder, fileName, viewIds, new DWGExportOptions());
                }
                else
                {
                    context.Document.Export(folder, fileName, viewIds, new DXFExportOptions());
                }
                data["exported"] = true;
                return data;
            }
            if (format == "ifc")
            {
                string folder = Path.GetDirectoryName(fullOutput);
                EnsureOutputDirectory(folder);
                string fileName = Path.GetFileNameWithoutExtension(fullOutput);
                var options = new IFCExportOptions();
                object filter = PlanValues.Get(step.Arguments, "filter_view_id", "filter_view");
                if (filter != null) options.FilterViewId = new ElementId(RevitLookups.ParsePositiveId(filter, "filter_view_id"));
                context.Document.Export(folder, fileName, options);
                data["exported"] = true;
                return data;
            }
            throw new BridgeCommandException("export.format 仅支持 image、png、jpg、dwg、dxf、ifc、schedule_csv。");
        }

        /// <summary>
        /// 保存或另存当前 Revit 项目文档。
        /// Save or save-as the current Revit project document.
        /// </summary>
        public static Dictionary<string, object> SaveDocument(PlanStep step, PlanExecutionContext context)
        {
            string requestedPath = PlanValues.String(step.Arguments, null, "path", "save_path", "output_path");
            bool overwrite = PlanValues.Boolean(step.Arguments, false, "overwrite_file", "overwrite");
            string currentPath = context.Document.PathName ?? string.Empty;
            string target = string.IsNullOrWhiteSpace(requestedPath) ? currentPath : Path.GetFullPath(requestedPath);
            var data = new Dictionary<string, object>
            {
                { "current_path", currentPath },
                { "path", target },
                { "overwrite_file", overwrite }
            };
            if (string.IsNullOrWhiteSpace(target))
            {
                throw new BridgeCommandException("当前项目尚未保存；请传 save_document.path（.rvt）。");
            }
            if (context.Preview) return data;
            if (!string.IsNullOrWhiteSpace(requestedPath))
            {
                if (!string.Equals(Path.GetExtension(target), ".rvt", StringComparison.OrdinalIgnoreCase))
                {
                    throw new BridgeCommandException("save_document.path 必须以 .rvt 结尾。");
                }
                if (File.Exists(target) && !overwrite)
                {
                    throw new BridgeCommandException("目标文件已存在；设置 overwrite_file=true 才会覆盖：" + target);
                }
                EnsureOutputDirectory(Path.GetDirectoryName(target));
                context.Document.SaveAs(target, new SaveAsOptions { OverwriteExistingFile = overwrite });
            }
            else
            {
                context.Document.Save();
            }
            data["saved"] = true;
            data["path"] = context.Document.PathName ?? target;
            return data;
        }

        /// <summary>
        /// 解析视图参数（支持按 ID 或者名称查找，可回退到活动视图）。
        /// Resolve a view from arguments by ID or name, optionally falling back to the active view.
        /// </summary>
        private static View ResolveView(
            PlanExecutionContext context,
            IDictionary<string, object> arguments,
            bool useActiveWhenMissing,
            params string[] names)
        {
            // 若参数未提供且 useActiveWhenMissing 为 true，回退到活动视图
            // Fall back to active view when arguments are missing and useActiveWhenMissing is true
            object raw = PlanValues.Get(arguments, names);
            if (raw == null && useActiveWhenMissing)
            {
                return context.Document.ActiveView;
            }
            ElementId id = context.ResolveSingleElementId(arguments, names);
            if (id.GetValue() == ElementId.InvalidElementId.GetValue())
            {
                return null;
            }
            View view = context.Document.GetElement(id) as View;
            if (view == null || view.IsTemplate)
            {
                throw new BridgeCommandException("参数 " + string.Join("/", names) + " 必须指向有效非样板视图。");
            }
            return view;
        }

        /// <summary>
        /// 解析视图族类型（按 type_id 或者 type_name 查找，匹配指定 ViewFamily）。
        /// Resolve a ViewFamilyType by type_id or type_name, matching the required ViewFamily.
        /// </summary>
        private static ViewFamilyType ResolveViewFamilyType(
            Document document,
            IDictionary<string, object> arguments,
            ViewFamily family)
        {
            // 优先按 type_id 直接查找，否则按 type_name 模糊匹配
            // Prefer direct lookup by type_id, then fall back to fuzzy match by type_name
            object rawId = PlanValues.Get(arguments, "type_id", "view_type_id");
            if (rawId != null)
            {
                ViewFamilyType type = document.GetElement(
                    new ElementId(RevitLookups.ParsePositiveId(rawId, "view_type_id"))) as ViewFamilyType;
                if (type == null || type.ViewFamily != family)
                {
                    throw new BridgeCommandException("view_type_id 不是 " + family + " 视图类型。");
                }
                return type;
            }
            string requested = PlanValues.String(arguments, null, "type", "type_name", "view_type_name");
            IEnumerable<ViewFamilyType> candidates = new FilteredElementCollector(document)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .Where(candidate => candidate.ViewFamily == family)
                .OrderBy(candidate => candidate.Name);
            if (!string.IsNullOrWhiteSpace(requested))
            {
                candidates = candidates.Where(candidate =>
                    string.Equals(candidate.Name, requested, StringComparison.OrdinalIgnoreCase));
            }
            ViewFamilyType result = candidates.FirstOrDefault();
            if (result == null)
            {
                throw new BridgeCommandException("当前项目没有可用 " + family + " 视图类型。");
            }
            return result;
        }

        /// <summary>
        /// 解析文字注释类型（按 text_type_id 或 text_type_name 查找）。
        /// Resolve a TextNoteType by text_type_id or text_type_name.
        /// </summary>
        private static TextNoteType ResolveTextNoteType(Document document, IDictionary<string, object> arguments)
        {
            object rawId = PlanValues.Get(arguments, "text_type_id", "text_note_type_id", "type_id");
            if (rawId != null)
            {
                TextNoteType byId = document.GetElement(
                    new ElementId(RevitLookups.ParsePositiveId(rawId, "text_type_id"))) as TextNoteType;
                if (byId == null) throw new BridgeCommandException("text_type_id 不是有效文字类型。");
                return byId;
            }
            string requested = PlanValues.String(arguments, null, "text_type", "text_type_name", "type");
            IEnumerable<TextNoteType> types = new FilteredElementCollector(document)
                .OfClass(typeof(TextNoteType)).Cast<TextNoteType>().OrderBy(type => type.Name);
            if (!string.IsNullOrWhiteSpace(requested))
            {
                types = types.Where(type => string.Equals(type.Name, requested, StringComparison.OrdinalIgnoreCase));
            }
            TextNoteType result = types.FirstOrDefault();
            if (result == null) throw new BridgeCommandException("当前项目没有可用文字类型。");
            return result;
        }

        /// <summary>
        /// 解析必填的正值元素 ID。
        /// Resolve a required positive ElementId from arguments.
        /// </summary>
        private static ElementId ResolvePositiveElementId(IDictionary<string, object> arguments, params string[] names)
        {
            object raw = PlanValues.Get(arguments, names);
            if (raw == null) throw new BridgeCommandException("缺少参数：" + string.Join("/", names));
            return new ElementId(RevitLookups.ParsePositiveId(raw, names[0]));
        }

        /// <summary>
        /// 解析可选的元素 ID（返回 null 表示未提供）。
        /// Resolve an optional ElementId from arguments (returns null if not provided).
        /// </summary>
        private static ElementId ResolveOptionalElementId(IDictionary<string, object> arguments, params string[] names)
        {
            object raw = PlanValues.Get(arguments, names);
            return raw == null ? null : new ElementId(RevitLookups.ParsePositiveId(raw, names[0]));
        }

        /// <summary>
        /// 读取向量参数（默认值可选，零向量会抛出异常）。
        /// Read a vector from arguments with a default value; zero vectors are rejected.
        /// </summary>
        private static XYZ ReadVector(IDictionary<string, object> arguments, string fieldName, XYZ defaultValue)
        {
            object raw = PlanValues.Get(arguments, fieldName);
            if (raw == null) return defaultValue;
            Dictionary<string, object> value = PlanValues.Dictionary(raw, fieldName);
            XYZ vector = new XYZ(
                PlanValues.Number(value, 0.0, "x"),
                PlanValues.Number(value, 0.0, "y"),
                PlanValues.Number(value, 0.0, "z"));
            if (vector.GetLength() < 1e-8) throw new BridgeCommandException(fieldName + " 不能为零向量。");
            return vector;
        }

        /// <summary>
        /// 从单点字典解析 XYZ（x/y 必填毫米值，z 可选）。
        /// Parse an XYZ point from a dictionary (x/y required in mm, z optional).
        /// </summary>
        private static XYZ ReadPointValue(IDictionary<string, object> value)
        {
            return new XYZ(
                PlanValues.ToFeet(PlanValues.RequireMillimeters(value, "x", "x_mm")),
                PlanValues.ToFeet(PlanValues.RequireMillimeters(value, "y", "y_mm")),
                PlanValues.ToFeet(PlanValues.Millimeters(value, 0.0, "z", "z_mm")));
        }

        /// <summary>
        /// 将 XYZ 向量转换为可序列化的字典（四舍五入到 6 位小数）。
        /// Convert an XYZ vector to a serializable dictionary (rounded to 6 decimal places).
        /// </summary>
        private static Dictionary<string, object> VectorData(XYZ value)
        {
            return new Dictionary<string, object>
            {
                { "x", Math.Round(value.X, 6) },
                { "y", Math.Round(value.Y, 6) },
                { "z", Math.Round(value.Z, 6) }
            };
        }

        /// <summary>
        /// 读取裁剪框 BoundingBoxXYZ（需要 min 和 max 两个点，且 min 须小于 max）。
        /// Read a BoundingBoxXYZ from arguments (requires min and max points; min must be less than max).
        /// </summary>
        private static BoundingBoxXYZ ReadBoundingBox(IDictionary<string, object> arguments, params string[] names)
        {
            Dictionary<string, object> raw = PlanValues.Dictionary(PlanValues.Get(arguments, names), names[0]);
            XYZ min = PlanValues.Point( raw, "min");
            XYZ max = PlanValues.Point(raw, "max");
            if (min.X >= max.X || min.Y >= max.Y || min.Z >= max.Z)
            {
                throw new BridgeCommandException("crop_box.min 必须小于 crop_box.max。");
            }
            return new BoundingBoxXYZ { Min = min, Max = max, Transform = Transform.Identity };
        }

        /// <summary>
        /// 解析导出目标视图 ID 列表（支持按 view_ids 或 active_view 标记）。
        /// Resolve target view IDs for export (by explicit view_ids or active_view flag).
        /// </summary>
        private static List<ElementId> ResolveExportViewIds(PlanStep step, PlanExecutionContext context)
        {
            object raw = PlanValues.Get(step.Arguments, "view_ids", "views");
            if (raw == null && PlanValues.Boolean(step.Arguments, false, "active_view"))
            {
                return new List<ElementId> { context.Document.ActiveView.Id };
            }
            if (raw == null) return new List<ElementId>();
            return context.ResolveElementIds(step.Arguments, "view_ids", "views").ToList();
        }

        /// <summary>
        /// 解析图片导出格式（默认 PNG 或 JPEGMedium）。
        /// Parse the image file type for export (defaults to PNG or JPEGMedium).
        /// </summary>
        private static ImageFileType ParseImageFileType(string format, IDictionary<string, object> arguments)
        {
            string requested = PlanValues.String(arguments, null, "image_type", "file_type");
            if (string.IsNullOrWhiteSpace(requested))
            {
                requested = format == "jpg" || format == "jpeg" ? "JPEGMedium" : "PNG";
            }
            ImageFileType result;
            if (!Enum.TryParse(requested, true, out result))
            {
                throw new BridgeCommandException("image_type 无效：" + requested);
            }
            return result;
        }

        /// <summary>
        /// 从参数中读取字符串列表（去空、去空白）。
        /// Read a list of strings from arguments (trimmed, empty entries filtered).
        /// </summary>
        private static List<string> ReadStringList(IDictionary<string, object> arguments, params string[] names)
        {
            object raw = PlanValues.Get(arguments, names);
            if (raw == null) return new List<string>();
            List<object> values = PlanValues.List(raw, names[0]);
            var result = new List<string>();
            foreach (object value in values)
            {
                string text = Convert.ToString(value, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text)) result.Add(text.Trim());
            }
            return result;
        }

        /// <summary>
        /// 从参数中解析枚举值（不区分大小写），失败时返回默认值。
        /// Parse an enum value from arguments (case-insensitive), returning a default on missing.
        /// </summary>
        private static T ParseEnum<T>(IDictionary<string, object> arguments, string name, T defaultValue)
            where T : struct
        {
            object raw = PlanValues.Get(arguments, name);
            if (raw == null) return defaultValue;
            T result;
            if (!Enum.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), true, out result))
            {
                throw new BridgeCommandException(name + " 无效：" + raw);
            }
            return result;
        }

        /// <summary>
        /// 设置视图名称（非空时重命名）。
        /// Set the view name if the provided name is not null or whitespace.
        /// </summary>
        private static void SetOptionalViewName(View view, string name)
        {
            if (!string.IsNullOrWhiteSpace(name)) view.Name = name.Trim();
        }

        /// <summary>
        /// 应用可选的视图样板（按 ID 或名称查找）。
        /// Apply an optional view template, resolved by ID or name.
        /// </summary>
        private static void ApplyOptionalViewTemplate(PlanExecutionContext context, View view, object rawTemplateId, string templateName)
        {
            ElementId templateId = ElementId.InvalidElementId;
            if (rawTemplateId != null)
            {
                templateId = new ElementId(RevitLookups.ParsePositiveId(rawTemplateId, "view_template_id"));
            }
            else if (!string.IsNullOrWhiteSpace(templateName))
            {
                View template = new FilteredElementCollector(context.Document)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .FirstOrDefault(candidate => candidate.IsTemplate &&
                        string.Equals(candidate.Name, templateName, StringComparison.OrdinalIgnoreCase));
                if (template == null)
                {
                    throw new BridgeCommandException("找不到视图样板：“" + templateName + "”。");
                }
                templateId = template.Id;
            }
            if (templateId.GetValue() == ElementId.InvalidElementId.GetValue())
            {
                return;
            }
            View templateElement = context.Document.GetElement(templateId) as View;
            if (templateElement == null || !templateElement.IsTemplate)
            {
                throw new BridgeCommandException("view_template_id 必须指向视图样板。");
            }
            view.ViewTemplateId = templateId;
        }

        /// <summary>
        /// 确保输出目录存在（不存在则创建）。
        /// Ensure the output directory exists, creating it if necessary.
        /// </summary>
        private static void EnsureOutputDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new BridgeCommandException("输出路径缺少有效目录。");
            Directory.CreateDirectory(directory);
        }
    }
}
