using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace RevitCommandBridge
{
    /// <summary>
    /// 无人值守计划事务的失败预处理器：
    /// Warning 直接消除并记录文本；Error 尝试默认解决方案；无法解决的错误回滚整个计划。
    /// </summary>
    internal sealed class BridgeFailurePreprocessor : IFailuresPreprocessor
    {
        public List<string> Messages { get; private set; }

        public BridgeFailurePreprocessor()
        {
            Messages = new List<string>();
        }

        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            bool hasUnresolvedError = false;
            foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages().ToList())
            {
                FailureSeverity severity = failure.GetSeverity();
                string description = failure.GetDescriptionText() ?? string.Empty;
                Messages.Add(severity + ": " + description.Trim());

                if (severity == FailureSeverity.Warning)
                {
                    failuresAccessor.DeleteWarning(failure);
                    continue;
                }

                if (severity == FailureSeverity.Error || severity == FailureSeverity.DocumentCorruption)
                {
                    hasUnresolvedError = true;
                }
            }

            if (hasUnresolvedError)
            {
                return FailureProcessingResult.ProceedWithRollBack;
            }
            return FailureProcessingResult.Continue;
        }
    }
}
