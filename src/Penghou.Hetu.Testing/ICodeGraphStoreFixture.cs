namespace Penghou.Hetu.Testing;

/// <summary>Creates an isolated graph store for conformance verification.</summary>
public interface ICodeGraphStoreFixture
{
    ICodeGraphStore CreateStore();
}
