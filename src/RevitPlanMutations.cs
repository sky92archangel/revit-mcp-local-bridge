using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitCommandBridge
{
    internal static class RevitPlanMutations
    {
        public static Dictionary<string, object> SetParameters(PlanStep step, PlanExecutionContext context)
        {
            IList<ElementId> ids = context.ResolveElementIds(step.Arguments, "element_ids", "elements", "targets");
            if (ids.Count == 0 && context.Preview)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
            }
            Dictionary<string, object> requested = PlanValues.Dictionary(step.Arguments, "parameters", true);
            if (requested.Count == 0)
            {
                throw new BridgeCommandException("set_parameters.parameters 不能为空。");
            }
            bool ignoreMissing = PlanValues.Boolean(step.Arguments, false, "ignore_missing");
            bool ignoreReadOnly = PlanValues.Boolean(step.Arguments, false, "ignore_read_only");
            var changes = new List<Dictionary<string, object>>();

            foreach (ElementId id in ids)
            {
                Element element = context.Document.GetElement(id);
                if (element == null)
                {
                    throw new BridgeCommandException("找不到 element_id=" + id.Value + " 对应元素。");
                }
                var changed = new List<string>();
                foreach (KeyValuePair<string, object> pair in requested)
                {
                    Parameter parameter = FindParameter(element, pair.Key);
                    if (parameter == null)
                    {
                        if (ignoreMissing)
                        {
                            continue;
                        }
                        throw new BridgeCommandException("元素 " + id.Value + " 找不到参数“" + pair.Key + "”。");
                    }
                    if (parameter.IsReadOnly)
                    {
                        if (ignoreReadOnly)
                        {
                            continue;
                        }
                        throw new BridgeCommandException("元素 " + id.Value + " 的参数“" + pair.Key + "”是只读。");
                    }

                    ValidateParameterValue(parameter, pair.Value, pair.Key);
                    if (!context.Preview)
                    {
                        SetParameterValue(parameter, pair.Value, pair.Key);
                    }
                    changed.Add(pair.Key);
                }
                changes.Add(new Dictionary<string, object>
                {
                    { "element_id", id.Value },
                    { "parameters", changed.ToArray() }
                });
            }

            return new Dictionary<string, object>
            {
                { "element_ids", ids.Select(id => id.Value).ToArray() },
                { "changes", changes },
                { "preview", context.Preview }
            };
        }

        public static Dictionary<string, object> DeleteElements(PlanStep step, PlanExecutionContext context)
        {
            IList<ElementId> ids = context.ResolveElementIds(step.Arguments, "element_ids", "elements", "targets");
            if (ids.Count == 0 && context.Preview)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
            }
            foreach (ElementId id in ids)
            {
                if (context.Document.GetElement(id) == null)
                {
                    throw new BridgeCommandException("找不到待删除 element_id=" + id.Value + "。");
                }
            }
            var data = new Dictionary<string, object>
            {
                { "requested_element_ids", ids.Select(id => id.Value).ToArray() }
            };
            if (context.Preview)
            {
                return data;
            }
            ICollection<ElementId> deleted = context.Document.Delete(ids);
            data["deleted_element_ids"] = deleted.Select(id => id.Value).ToArray();
            return data;
        }

        public static Dictionary<string, object> SelectElements(PlanStep step, PlanExecutionContext context)
        {
            IList<ElementId> ids = context.ResolveElementIds(step.Arguments, "element_ids", "elements", "targets");
            if (ids.Count == 0 && context.Preview)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
            }
            foreach (ElementId id in ids)
            {
                if (context.Document.GetElement(id) == null)
                {
                    throw new BridgeCommandException("找不到待选择 element_id=" + id.Value + "。");
                }
            }
            bool show = PlanValues.Boolean(step.Arguments, true, "show", "zoom");
            if (!context.Preview)
            {
                context.RequestSelection(ids, show);
            }
            return new Dictionary<string, object>
            {
                { "element_ids", ids.Select(id => id.Value).ToArray() },
                { "show", show },
                { "preview", context.Preview }
            };
        }

        public static Dictionary<string, object> DuplicateType(PlanStep step, PlanExecutionContext context)
        {
            ElementId sourceId = context.ResolveSingleElementId(
                step.Arguments, "type_id", "source_type_id", "element_id", "target");
            if (sourceId.Value == ElementId.InvalidElementId.Value)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置类型引用尚无真实 ID。" } };
            }
            ElementType sourceType = context.Document.GetElement(sourceId) as ElementType;
            if (sourceType == null)
            {
                throw new BridgeCommandException("duplicate_type 的 type_id 必须指向 ElementType（类型）。");
            }
            string newName = PlanValues.String(step.Arguments, null, "new_name", "name");
            if (string.IsNullOrWhiteSpace(newName))
            {
                newName = sourceType.Name + "_副本";
            }
            Dictionary<string, object> requested = PlanValues.Dictionary(step.Arguments, "parameters", false);
            var data = new Dictionary<string, object>
            {
                { "source_type_id", sourceId.Value },
                { "source_name", sourceType.Name },
                { "new_name", newName },
                { "parameter_count", requested.Count }
            };
            // preview 下对源类型校验参数名，尽早暴露拼写错误。
            foreach (KeyValuePair<string, object> pair in requested)
            {
                Parameter parameter = FindParameter(sourceType, pair.Key);
                if (parameter == null)
                {
                    throw new BridgeCommandException("类型 " + sourceId.Value + " 找不到参数“" + pair.Key + "”。");
                }
                if (parameter.IsReadOnly)
                {
                    throw new BridgeCommandException("类型 " + sourceId.Value + " 的参数“" + pair.Key + "”是只读。");
                }
                ValidateParameterValue(parameter, pair.Value, pair.Key);
            }
            if (context.Preview)
            {
                return data;
            }

            ElementId newTypeId;
            try
            {
                newTypeId = sourceType.Duplicate(newName).Id;
            }
            catch (Exception ex)
            {
                throw new BridgeCommandException("复制类型“" + sourceType.Name + "”失败（名称可能已存在）：" + ex.Message);
            }
            ElementType newType = context.Document.GetElement(newTypeId) as ElementType;
            if (newType == null)
            {
                throw new BridgeCommandException("Revit 未返回复制后的类型。");
            }
            foreach (KeyValuePair<string, object> pair in requested)
            {
                SetParameterValue(FindParameter(newType, pair.Key), pair.Value, pair.Key);
            }
            data["element_id"] = newTypeId.Value;
            data["element_ids"] = new[] { newTypeId.Value };
            data["new_name"] = newType.Name;
            return data;
        }

        public static Dictionary<string, object> ManageSchemaData(PlanStep step, PlanExecutionContext context)
        {
            string action = PlanValues.String(step.Arguments, null, "action").Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(action))
            {
                action = "get";
            }
            if (action != "set" && action != "get" && action != "clear" && action != "transport")
            {
                throw new BridgeCommandException("manage_schema_data.action 仅支持 set、get、clear、transport。");
            }

            Dictionary<string, string> values = null;
            if (action == "set")
            {
                Dictionary<string, object> raw = PlanValues.Dictionary(step.Arguments, "values", true);
                if (raw.Count == 0)
                {
                    throw new BridgeCommandException("manage_schema_data.set 的 values 不能为空。");
                }
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, object> pair in raw)
                {
                    values[pair.Key] = Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty;
                }
            }

            ElementId sourceId = ElementId.InvalidElementId;
            if (action == "transport")
            {
                sourceId = context.ResolveSingleElementId(step.Arguments, "source_element_id", "source");
                if (sourceId.Value == ElementId.InvalidElementId.Value)
                {
                    return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
                }
            }

            bool single = action == "get";
            IList<ElementId> targets;
            if (single)
            {
                ElementId id = context.ResolveSingleElementId(step.Arguments, "element_id", "element", "target");
                if (id.Value == ElementId.InvalidElementId.Value)
                {
                    return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
                }
                targets = new List<ElementId> { id };
            }
            else
            {
                targets = context.ResolveElementIds(step.Arguments, "element_ids", "elements", "targets");
                if (targets.Count == 0)
                {
                    return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
                }
            }

            var data = new Dictionary<string, object>
            {
                { "action", action },
                { "target_count", targets.Count }
            };
            if (values != null)
            {
                data["values"] = values;
            }
            if (context.Preview)
            {
                return data;
            }

            if (action == "transport")
            {
                Element source = context.Document.GetElement(sourceId);
                if (source == null)
                {
                    throw new BridgeCommandException("找不到 source_element_id=" + sourceId.Value + "。");
                }
                values = BridgeSchemas.ReadMap(source);
                if (values == null || values.Count == 0)
                {
                    throw new BridgeCommandException("源元素没有可搬运的扩展数据：" + sourceId.Value);
                }
                data["values"] = values;
            }

            switch (action)
            {
                case "set":
                case "transport":
                    foreach (ElementId id in targets)
                    {
                        Element element = context.Document.GetElement(id);
                        if (element == null)
                        {
                            throw new BridgeCommandException("找不到 element_id=" + id.Value + "。");
                        }
                        BridgeSchemas.WriteMap(element, values);
                    }
                    data["written"] = targets.Count;
                    break;
                case "get":
                {
                    Element element = context.Document.GetElement(targets[0]);
                    if (element == null)
                    {
                        throw new BridgeCommandException("找不到 element_id=" + targets[0].Value + "。");
                    }
                    data["values"] = BridgeSchemas.ReadMap(element);
                    break;
                }
                case "clear":
                    foreach (ElementId id in targets)
                    {
                        Element element = context.Document.GetElement(id);
                        if (element == null)
                        {
                            throw new BridgeCommandException("找不到 element_id=" + id.Value + "。");
                        }
                        element.DeleteEntity(BridgeSchemas.AiMetadata);
                    }
                    data["cleared"] = targets.Count;
                    break;
            }
            data["element_ids"] = targets.Select(id => id.Value).ToArray();
            return data;
        }

        public static Dictionary<string, object> ManageFamilyParameters(PlanStep step, PlanExecutionContext context)
        {
            ElementId familyId = context.ResolveSingleElementId(step.Arguments, "family_id", "family", "target");
            if (familyId.Value == ElementId.InvalidElementId.Value)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置族引用尚无真实 ID。" } };
            }
            Family family = context.Document.GetElement(familyId) as Family;
            if (family == null)
            {
                throw new BridgeCommandException("manage_family_parameters 的 family_id 必须指向族（Family）。");
            }
            List<Dictionary<string, object>> actions = PlanValues.DictionaryList(
                PlanValues.Get(step.Arguments, "actions"), "manage_family_parameters.actions");
            if (actions.Count == 0)
            {
                throw new BridgeCommandException("manage_family_parameters 至少需要一个 actions 项。");
            }

            var described = new List<string>();
            foreach (Dictionary<string, object> item in actions)
            {
                string verb = PlanValues.String(item, null, "action").Trim().ToLowerInvariant();
                string name = PlanValues.String(item, null, "name", "parameter");
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new BridgeCommandException("actions[].name 不能为空。");
                }
                switch (verb)
                {
                    case "add":
                        RevitParameterAdmin.NormalizeSpecToken(PlanValues.String(item, "length", "type"));
                        described.Add("add " + name);
                        break;
                    case "rename":
                        if (string.IsNullOrWhiteSpace(PlanValues.String(item, null, "new_name")))
                        {
                            throw new BridgeCommandException("rename 动作需要 new_name。");
                        }
                        described.Add("rename " + name);
                        break;
                    case "remove":
                        described.Add("remove " + name);
                        break;
                    case "set_formula":
                        if (string.IsNullOrWhiteSpace(PlanValues.String(item, null, "formula")))
                        {
                            throw new BridgeCommandException("set_formula 动作需要 formula。");
                        }
                        described.Add("set_formula " + name);
                        break;
                    default:
                        throw new BridgeCommandException("actions[].action 仅支持 add、rename、remove、set_formula。");
                }
            }
            var data = new Dictionary<string, object>
            {
                { "family", family.Name },
                { "family_id", familyId.Value },
                { "actions", described.ToArray() }
            };
            if (context.Preview)
            {
                return data;
            }

            Document familyDocument = context.Document.EditFamily(family);
            using (Transaction familyTransaction = new Transaction(familyDocument, "RCB manage_family_parameters"))
            {
                TransactionStatus started = familyTransaction.Start();
                if (started != TransactionStatus.Started)
                {
                    throw new BridgeCommandException("族文档事务启动失败：" + started);
                }
                try
                {
                    FamilyManager manager = familyDocument.FamilyManager;
                    foreach (Dictionary<string, object> item in actions)
                    {
                        string verb = PlanValues.String(item, null, "action").Trim().ToLowerInvariant();
                        string name = PlanValues.String(item, null, "name", "parameter");
                        try
                        {
                            switch (verb)
                            {
                                case "add":
                                    RevitParameterAdmin.AddFamilyParameter(
                                        manager,
                                        name,
                                        PlanValues.String(item, "length", "type"),
                                        PlanValues.String(item, "data", "group", "parameter_group"),
                                        PlanValues.Boolean(item, false, "is_instance", "instance"));
                                    break;
                                case "rename":
                                    manager.RenameParameter(
                                        manager.get_Parameter(name), PlanValues.String(item, null, "new_name"));
                                    break;
                                case "remove":
                                    manager.RemoveParameter(manager.get_Parameter(name));
                                    break;
                                case "set_formula":
                                    manager.SetFormula(manager.get_Parameter(name), PlanValues.String(item, null, "formula"));
                                    break;
                            }
                        }
                        catch (BridgeCommandException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            throw new BridgeCommandException("族参数动作“" + verb + " " + name + "”失败：" + ex.Message);
                        }
                    }
                    familyTransaction.Commit();
                }
                catch
                {
                    if (familyTransaction.GetStatus() == TransactionStatus.Started)
                    {
                        familyTransaction.RollBack();
                    }
                    throw;
                }
            }
Family loadedFamilyResult;
            if (!context.Document.LoadFamily(familyDocument.PathName, new BridgeFamilyLoadOptions(), out loadedFamilyResult))
            {
                throw new BridgeCommandException("族“" + family.Name + "”回载到项目失败。");
            }
            data["applied"] = actions.Count;
            data["element_id"] = familyId.Value;
            data["element_ids"] = new[] { familyId.Value };
            return data;
        }

        public static Dictionary<string, object> TransformElements(PlanStep step, PlanExecutionContext context)
        {
            IList<ElementId> ids = context.ResolveElementIds(step.Arguments, "element_ids", "elements", "targets");
            if (ids.Count == 0)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
            }
            foreach (ElementId id in ids)
            {
                if (context.Document.GetElement(id) == null)
                {
                    throw new BridgeCommandException("找不到待变换 element_id=" + id.Value + "。");
                }
            }
            string mode = PlanValues.String(step.Arguments, null, "mode").Trim().ToLowerInvariant();
            var data = new Dictionary<string, object>
            {
                { "mode", mode },
                { "target_count", ids.Count },
                { "element_ids", ids.Select(id => id.Value).ToArray() }
            };
            if (context.Preview)
            {
                return data;
            }

            Document document = context.Document;
            switch (mode)
            {
                case "move":
                    XYZ translation = ReadPoint(step.Arguments, "translation", "vector");
                    if (translation.GetLength() < 1e-9)
                    {
                        throw new BridgeCommandException("transform_elements.move 的 translation 不能为零向量。");
                    }
                    ElementTransformUtils.MoveElements(document, ids, translation);
                    data["translation"] = PlanValues.PointData(translation);
                    break;
                case "copy":
                    XYZ offset = ReadPoint(step.Arguments, "translation", "vector", "offset");
                    if (offset.GetLength() < 1e-9)
                    {
                        throw new BridgeCommandException("transform_elements.copy 的 translation 不能为零向量。");
                    }
                    ICollection<ElementId> copied = ElementTransformUtils.CopyElements(document, ids, offset);
                    data["copied_count"] = copied.Count;
                    data["element_ids"] = copied.Select(id => id.Value).ToArray();
                    break;
                case "rotate":
                    XYZ origin = ReadPoint(step.Arguments, "axis_origin", "origin");
                    XYZ direction = ReadAxisDirection(step.Arguments);
                    double angleDegrees = PlanValues.Number(step.Arguments, 0.0, "angle", "angle_deg", "rotation");
                    if (Math.Abs(angleDegrees) < 1e-9)
                    {
                        throw new BridgeCommandException("transform_elements.rotate 的 angle 不能为 0。");
                    }
                    ElementTransformUtils.RotateElements(
                        document, ids, Line.CreateUnbound(origin, direction), PlanValues.ToRadians(angleDegrees));
                    data["angle"] = angleDegrees;
                    data["axis_origin"] = PlanValues.PointData(origin);
                    break;
                case "mirror":
                    XYZ planePoint = ReadPoint(step.Arguments, "plane_point", "origin");
                    XYZ normal = ReadPoint(step.Arguments, "plane_normal", "normal");
                    if (normal.GetLength() < 1e-9)
                    {
                        throw new BridgeCommandException("transform_elements.mirror 的 plane_normal 不能为零向量。");
                    }
                    Plane plane = Plane.CreateByNormalAndOrigin(normal.Normalize(), planePoint);
                    ICollection<ElementId> mirrored = ElementTransformUtils.MirrorElements(document, ids, plane, true);
                    data["mirrored_count"] = mirrored.Count;
                    data["element_ids"] = mirrored.Select(id => id.Value).ToArray();
                    break;
                default:
                    throw new BridgeCommandException("transform_elements.mode 仅支持 move、copy、rotate、mirror。");
            }
            return data;
        }

        public static Dictionary<string, object> RenameElement(PlanStep step, PlanExecutionContext context)
        {
            IList<ElementId> ids = context.ResolveElementIds(step.Arguments, "element_ids", "elements", "targets", "element_id");
            if (ids.Count == 0)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
            }
            string name = PlanValues.String(step.Arguments, null, "name", "new_name");
            string prefix = PlanValues.String(step.Arguments, null, "prefix");
            bool withIdSuffix = PlanValues.Boolean(step.Arguments, false, "id_suffix", "append_id");
            if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(prefix))
            {
                throw new BridgeCommandException("rename_element 需要 name（单目标）或 prefix（批量模式）。");
            }
            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(prefix))
            {
                throw new BridgeCommandException("rename_element 的 name 与 prefix 只能二选一。");
            }
            if (!string.IsNullOrWhiteSpace(name) && ids.Count > 1)
            {
                throw new BridgeCommandException("rename_element.name 模式只支持单个目标；批量请使用 prefix。");
            }

            var data = new Dictionary<string, object>
            {
                { "target_count", ids.Count }
            };
            if (context.Preview)
            {
                data["mode"] = string.IsNullOrWhiteSpace(prefix) ? "name" : (withIdSuffix ? "prefix+id" : "prefix");
                return data;
            }

            var renamed = new List<Dictionary<string, object>>();
            foreach (ElementId id in ids)
            {
                Element element = context.Document.GetElement(id);
                if (element == null)
                {
                    throw new BridgeCommandException("找不到待重命名 element_id=" + id.Value + "。");
                }
                string oldName = RevitLookups.ElementName(element);
                string newName = string.IsNullOrWhiteSpace(prefix)
                    ? name
                    : (withIdSuffix ? prefix + id.Value : prefix + oldName);
                try
                {
                    element.Name = newName;
                }
                catch (Exception ex)
                {
                    throw new BridgeCommandException(
                        "重命名 element_id=" + id.Value + "（" + oldName + " → " + newName + "）失败：" + ex.Message);
                }
                renamed.Add(new Dictionary<string, object>
                {
                    { "element_id", id.Value },
                    { "old_name", oldName },
                    { "new_name", RevitLookups.ElementName(element) }
                });
            }
            data["renamed"] = renamed;
            data["element_ids"] = ids.Select(id => id.Value).ToArray();
            return data;
        }

        public static Dictionary<string, object> SetElementCurve(PlanStep step, PlanExecutionContext context)
        {
            ElementId id = context.ResolveSingleElementId(step.Arguments, "element_id", "element", "target");
            if (id.Value == ElementId.InvalidElementId.Value)
            {
                return new Dictionary<string, object> { { "deferred", true }, { "reason", "preview 中前置元素引用尚无真实 ID。" } };
            }
            Element element = RequireElement(context.Document, id, "element_id");
            LocationCurve location = element.Location as LocationCurve;
            if (location == null)
            {
                throw new BridgeCommandException("set_element_curve 目标必须是线状图元（墙 / 管道 / 线管 / 桥架 / 模型线）。");
            }
            XYZ start = PlanValues.Point(step.Arguments, "start");
            XYZ end = PlanValues.Point(step.Arguments, "end");
            if (start.DistanceTo(end) < 1e-8)
            {
                throw new BridgeCommandException("set_element_curve 的 start 与 end 不能重合。");
            }
            var data = new Dictionary<string, object>
            {
                { "element_id", id.Value },
                { "start", PlanValues.PointData(start) },
                { "end", PlanValues.PointData(end) }
            };
            if (context.Preview)
            {
                return data;
            }

            location.Curve = Line.CreateBound(start, end);
            Parameter lengthParameter = element.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
            if (lengthParameter != null && !lengthParameter.IsReadOnly)
            {
                data["length_mm"] = PlanValues.ToMillimeters(location.Curve.Length);
            }
            data["element_ids"] = new[] { id.Value };
            return data;
        }

        private static XYZ ReadPoint(IDictionary<string, object> arguments, params string[] fieldNames)
        {
            foreach (string fieldName in fieldNames)
            {
                if (PlanValues.Get(arguments, fieldName) != null)
                {
                    return PlanValues.Point(arguments, fieldName);
                }
            }
            throw new BridgeCommandException("缺少参数：" + string.Join("/", fieldNames));
        }

        private static XYZ ReadAxisDirection(IDictionary<string, object> arguments)
        {
            object raw = PlanValues.Get(arguments, "axis_direction", "axis");
            if (raw == null)
            {
                return XYZ.BasisZ;
            }
            string text = raw as string;
            if (text != null)
            {
                switch (text.Trim().ToLowerInvariant())
                {
                    case "x": return XYZ.BasisX;
                    case "y": return XYZ.BasisY;
                    case "z": return XYZ.BasisZ;
                    default:
                        throw new BridgeCommandException("axis_direction 仅支持 x、y、z 或 {x,y,z} 向量。");
                }
            }
            Dictionary<string, object> values = PlanValues.Dictionary(raw, "axis_direction");
            XYZ direction = new XYZ(
                PlanValues.Number(values, 0.0, "x"),
                PlanValues.Number(values, 0.0, "y"),
                PlanValues.Number(values, 0.0, "z"));
            if (direction.GetLength() < 1e-9)
            {
                throw new BridgeCommandException("axis_direction 不能为零向量。");
            }
            return direction.Normalize();
        }

        private static Element RequireElement(Document document, ElementId id, string fieldName)
        {
            Element element = document.GetElement(id);
            if (element == null)
            {
                throw new BridgeCommandException("找不到 " + fieldName + "=" + id.Value + " 对应元素。");
            }
            return element;
        }

        private static Parameter FindParameter(Element element, string requestedName)
        {
            const string prefix = "BIP:";
            if (requestedName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                BuiltInParameter builtIn;
                if (!Enum.TryParse(requestedName.Substring(prefix.Length).Trim(), true, out builtIn))
                {
                    throw new BridgeCommandException("无效 BuiltInParameter：" + requestedName);
                }
                return element.get_Parameter(builtIn);
            }
            return element.LookupParameter(requestedName);
        }

        private static void ValidateParameterValue(Parameter parameter, object value, string name)
        {
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    ReadDoubleValue(value, name);
                    break;
                case StorageType.Integer:
                    ReadIntegerValue(value, name);
                    break;
                case StorageType.String:
                    if (value is IDictionary<string, object>)
                    {
                        throw new BridgeCommandException("字符串参数“" + name + "”不能使用单位对象。");
                    }
                    break;
                case StorageType.ElementId:
                    ReadElementIdValue(value, name);
                    break;
                default:
                    throw new BridgeCommandException("参数“" + name + "”的 StorageType 不受支持：" + parameter.StorageType);
            }
        }

        private static void SetParameterValue(Parameter parameter, object value, string name)
        {
            switch (parameter.StorageType)
            {
                case StorageType.Double:
                    parameter.Set(ReadDoubleValue(value, name));
                    return;
                case StorageType.Integer:
                    parameter.Set(ReadIntegerValue(value, name));
                    return;
                case StorageType.String:
                    parameter.Set(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                case StorageType.ElementId:
                    parameter.Set(ReadElementIdValue(value, name));
                    return;
                default:
                    throw new BridgeCommandException("参数“" + name + "”的 StorageType 不受支持：" + parameter.StorageType);
            }
        }

        private static double ReadDoubleValue(object raw, string name)
        {
            IDictionary<string, object> objectValue = raw as IDictionary<string, object>;
            if (objectValue == null)
            {
                return PlanValues.ParseNumber(raw, name);
            }
            object rawValue = PlanValues.Get(objectValue, "value");
            if (rawValue == null)
            {
                throw new BridgeCommandException("参数“" + name + "”的数值对象缺少 value。");
            }
            double value = PlanValues.ParseNumber(rawValue, name + ".value");
            string unit = PlanValues.String(objectValue, "internal", "unit").ToLowerInvariant();
            switch (unit)
            {
                case "internal":
                    return value;
                case "mm":
                case "millimeter":
                case "millimeters":
                    return PlanValues.ToFeet(value);
                case "m":
                case "meter":
                case "meters":
                    return PlanValues.ToFeet(value * 1000.0);
                case "ft":
                case "feet":
                    return value;
                case "deg":
                case "degree":
                case "degrees":
                    return value * Math.PI / 180.0;
                case "rad":
                case "radian":
                case "radians":
                    return value;
                default:
                    throw new BridgeCommandException("参数“" + name + "”不支持 unit=“" + unit + "”。支持 internal、mm、m、ft、deg、rad。");
            }
        }

        private static int ReadIntegerValue(object raw, string name)
        {
            if (raw is bool)
            {
                return (bool)raw ? 1 : 0;
            }
            int value;
            if (!int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                throw new BridgeCommandException("参数“" + name + "”必须是整数或布尔值。");
            }
            return value;
        }

        private static ElementId ReadElementIdValue(object raw, string name)
        {
            IDictionary<string, object> objectValue = raw as IDictionary<string, object>;
            if (objectValue != null)
            {
                raw = PlanValues.Get(objectValue, "element_id", "id", "value");
            }
            int id;
            if (!int.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out id))
            {
                throw new BridgeCommandException("参数“" + name + "”必须是元素 ID。");
            }
            return new ElementId(id);
        }
    }
}
