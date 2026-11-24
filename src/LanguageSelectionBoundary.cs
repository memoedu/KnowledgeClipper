namespace KnowledgeClipper;

internal static class LanguageSelectionBoundary
{
    internal static Workflow.SelectionContext.Result Complete(string selected, string prefix, string suffix) =>
        Workflow.SelectionContext.Complete(selected, prefix, suffix);
}
