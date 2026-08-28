using System.Text;

namespace Saydin.DatabaseMigrator;

internal static class SqlScriptNormalizer
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    public static string Normalize(MigrationDefinition migration)
    {
        string sql;
        try
        {
            sql = StrictUtf8.GetString(migration.RawBytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new MigratorRejectedException("migration_not_utf8", migration.FileName, ex);
        }

        var statements = Scan(sql, migration.FileName);
        if (statements.Count == 0)
            throw new MigratorRejectedException("sql_body_empty", migration.FileName);

        var transactionStatements = statements
            .Where(statement => IsTransactionControl(statement.Canonical))
            .ToArray();
        if (statements.Any(statement => IsNonTransactional(statement.Canonical)))
            throw new MigratorRejectedException("nontransactional_statement_unsupported", migration.FileName);
        if (transactionStatements.Length == 0)
            return sql;

        var first = statements[0];
        var last = statements[^1];
        if (!IsBegin(first.Canonical) || !IsCommit(last.Canonical) ||
            transactionStatements.Length != 2)
        {
            throw new MigratorRejectedException("transaction_control_unsupported", migration.FileName);
        }

        var chars = sql.ToCharArray();
        BlankNonNewlines(chars, first.ExecutableStart, first.EndExclusive);
        BlankNonNewlines(chars, last.ExecutableStart, last.EndExclusive);
        return new string(chars);
    }

    public static string DeferPinnedActivityCompressionPolicy(
        string sql,
        string fileName,
        string expectedCanonical)
    {
        var statements = Scan(sql, fileName);
        var candidates = statements.Where(statement =>
            statement.Canonical.StartsWith("selectadd_compression_policy(",
                StringComparison.Ordinal)).ToArray();
        if (candidates.Length != 1 ||
            !string.Equals(candidates[0].Canonical, expectedCanonical, StringComparison.Ordinal))
            throw new MigratorRejectedException("compression_policy_defer_contract_mismatch", fileName);
        var chars = sql.ToCharArray();
        BlankNonNewlines(chars, candidates[0].ExecutableStart, candidates[0].EndExclusive);
        return new string(chars);
    }

    private static List<Statement> Scan(string sql, string fileName)
    {
        var statements = new List<Statement>();
        var canonical = new StringBuilder();
        var executableStart = -1;
        var lineOnlyWhitespace = true;
        var blockCommentDepth = 0;
        string? dollarDelimiter = null;
        var state = LexState.Normal;

        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];
            var next = index + 1 < sql.Length ? sql[index + 1] : '\0';

            if (state == LexState.LineComment)
            {
                if (current == '\n')
                {
                    state = LexState.Normal;
                    lineOnlyWhitespace = true;
                }
                continue;
            }

            if (state == LexState.BlockComment)
            {
                if (current == '/' && next == '*')
                {
                    blockCommentDepth++;
                    index++;
                }
                else if (current == '*' && next == '/')
                {
                    blockCommentDepth--;
                    index++;
                    if (blockCommentDepth == 0)
                        state = LexState.Normal;
                }
                else if (current == '\n')
                {
                    lineOnlyWhitespace = true;
                }
                continue;
            }

            if (state == LexState.SingleQuote)
            {
                canonical.Append(char.ToLowerInvariant(current));
                if (current == '\n')
                    lineOnlyWhitespace = true;
                if (current == '\'' && next == '\'')
                {
                    canonical.Append(next);
                    index++;
                }
                else if (current == '\'')
                {
                    state = LexState.Normal;
                }
                continue;
            }

            if (state == LexState.DoubleQuote)
            {
                canonical.Append(char.ToLowerInvariant(current));
                if (current == '\n')
                    lineOnlyWhitespace = true;
                if (current == '"' && next == '"')
                {
                    canonical.Append(next);
                    index++;
                }
                else if (current == '"')
                {
                    state = LexState.Normal;
                }
                continue;
            }

            if (state == LexState.DollarQuote)
            {
                if (current == '\n')
                    lineOnlyWhitespace = true;
                if (dollarDelimiter is not null &&
                    sql.AsSpan(index).StartsWith(dollarDelimiter, StringComparison.Ordinal))
                {
                    index += dollarDelimiter.Length - 1;
                    dollarDelimiter = null;
                    state = LexState.Normal;
                }
                continue;
            }

            if (current == '\n')
            {
                lineOnlyWhitespace = true;
                continue;
            }
            if (char.IsWhiteSpace(current))
                continue;

            if (current == '-' && next == '-')
            {
                state = LexState.LineComment;
                index++;
                continue;
            }
            if (current == '/' && next == '*')
            {
                state = LexState.BlockComment;
                blockCommentDepth = 1;
                index++;
                continue;
            }
            if (current == '\\' && lineOnlyWhitespace)
                throw new MigratorRejectedException("psql_metacommand_unsupported", fileName);

            lineOnlyWhitespace = false;
            executableStart = executableStart < 0 ? index : executableStart;

            if (current == '\'')
            {
                canonical.Append(current);
                state = LexState.SingleQuote;
                continue;
            }
            if (current == '"')
            {
                canonical.Append(current);
                state = LexState.DoubleQuote;
                continue;
            }
            if (current == '$' && TryReadDollarDelimiter(sql, index, out var delimiter))
            {
                dollarDelimiter = delimiter;
                state = LexState.DollarQuote;
                index += delimiter.Length - 1;
                continue;
            }
            if (current == ';')
            {
                if (canonical.Length > 0)
                    statements.Add(new Statement(canonical.ToString(), executableStart, index + 1));
                canonical.Clear();
                executableStart = -1;
                continue;
            }

            canonical.Append(char.ToLowerInvariant(current));
        }

        if (state is LexState.SingleQuote or LexState.DoubleQuote or LexState.DollarQuote or LexState.BlockComment)
            throw new MigratorRejectedException("sql_lexically_unterminated", fileName);
        if (canonical.Length > 0)
            statements.Add(new Statement(canonical.ToString(), executableStart, sql.Length));

        return statements;
    }

    private static bool TryReadDollarDelimiter(string sql, int start, out string delimiter)
    {
        var end = start + 1;
        while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_'))
            end++;
        if (end < sql.Length && sql[end] == '$')
        {
            delimiter = sql[start..(end + 1)];
            return true;
        }

        delimiter = string.Empty;
        return false;
    }

    private static bool IsBegin(string canonical) =>
        canonical is "begin" or "begintransaction" or "beginwork";

    private static bool IsCommit(string canonical) =>
        canonical is "commit" or "committransaction" or "commitwork";

    private static bool IsTransactionControl(string canonical) =>
        IsBegin(canonical) || IsCommit(canonical) ||
        canonical is "rollback" or "rollbacktransaction" or "rollbackwork" or "starttransaction";

    private static bool IsNonTransactional(string canonical) =>
        canonical.StartsWith("createdatabase", StringComparison.Ordinal) ||
        canonical.StartsWith("dropdatabase", StringComparison.Ordinal) ||
        canonical.StartsWith("vacuum", StringComparison.Ordinal) ||
        canonical.StartsWith("cluster", StringComparison.Ordinal) ||
        canonical.StartsWith("altersystem", StringComparison.Ordinal) ||
        canonical.StartsWith("createindexconcurrently", StringComparison.Ordinal) ||
        canonical.StartsWith("createuniqueindexconcurrently", StringComparison.Ordinal) ||
        canonical.StartsWith("dropindexconcurrently", StringComparison.Ordinal) ||
        (canonical.StartsWith("reindex", StringComparison.Ordinal) &&
         canonical.Contains("concurrently", StringComparison.Ordinal));

    private static void BlankNonNewlines(char[] chars, int start, int endExclusive)
    {
        for (var index = start; index < endExclusive; index++)
        {
            if (chars[index] is not ('\r' or '\n'))
                chars[index] = ' ';
        }
    }

    private enum LexState
    {
        Normal,
        SingleQuote,
        DoubleQuote,
        DollarQuote,
        LineComment,
        BlockComment,
    }

    private sealed record Statement(string Canonical, int ExecutableStart, int EndExclusive);
}
