using ClipMetaCore.Mp4;
using ClipMetaCore.Schema;
using ClipMetaCore.Write;

namespace ClipMetaCore.Read;

/// <summary>
/// Turns a parsed source clip's clipmeta metadata into a <see cref="MetadataMutation"/> that, when
/// applied to a destination clip, copies those fields onto it. Pure (no IO) so the read→mutation
/// logic is unit-testable without touching the filesystem and the CLI stays a thin shell.
/// </summary>
public static class ClipMetaCopier
{
    /// <summary>
    /// Builds a <b>merge</b> mutation from <paramref name="source"/>: every clipmeta <i>user</i>
    /// field (the internal <c>schema</c> field excluded) becomes a <see cref="MetadataMutation.SetFields"/>
    /// entry keyed by its domain-qualified atom name. Applying the result to a destination sets
    /// those fields without disturbing any field the source does not carry.
    /// </summary>
    /// <param name="source">Root node of the source clip (from <see cref="Mp4Parser.ParseFile"/>).</param>
    /// <returns>A mutation whose <c>SetFields</c> mirror the source's user metadata.</returns>
    public static MetadataMutation BuildCopyMutation(BoxNode source)
    {
        var mutation = new MetadataMutation();
        foreach (var (field, value) in ClipMetaReader.GetUserFields(source))
            mutation.SetFields[ClipMetaSchema.AtomName(field)] = value;
        return mutation;
    }
}
