namespace RevitCommandBridge
{
    /// <summary>
    /// 无人值守计划事务的失败预处理器：
    /// Warning 直接消除并记录文本；Error 尝试默认解决方案；无法解决的错误回滚整个计划�?
    /// Failure preprocessor for unattended transaction groups:
    /// Warnings are dismissed and recorded as text; Errors attempt the default resolution; unresolvable errors roll back the entire transaction.
    /// </summary>
    internal sealed class BridgeFailurePreprocessor : IFailuresPreprocessor
    {
        /// <summary>
        /// 所有失败消息的收集列表（供后续读取）�?
        /// Collected list of all failure messages (for later inspection).
        /// </summary>
        public List<string> Messages { get; private set; }

        public BridgeFailurePreprocessor()
        {
            Messages = new List<string>();
        }

        /// <summary>
        /// 逐条处理 Revit 失败消息：Warning 直接删除，Error 标记为不可解决�?
        /// Processes Revit failure messages one by one: deletes warnings, marks errors as unresolvable.
        /// </summary>
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            bool hasUnresolvedError = false;
            // ToList 创建副本以避免迭代时修改集合
            // ToList creates a copy to avoid modifying the collection during iteration
            foreach (FailureMessageAccessor failure in failuresAccessor.GetFailureMessages().ToList())
            {
                FailureSeverity severity = failure.GetSeverity();
                string description = failure.GetDescriptionText() ?? string.Empty;
                Messages.Add(severity + ": " + description.Trim());

                // Warning 直接消除（不影响事务提交�?
                // Dismiss warnings directly (does not affect transaction commit)
                if (severity == FailureSeverity.Warning)
                {
                    failuresAccessor.DeleteWarning(failure);
                    continue;
                }

                // Error �?DocumentCorruption 无法自动解决，触发回�?
                // Error or DocumentCorruption cannot be auto-resolved, trigger rollback
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
