namespace FixtureLib;

// Deliberately planted CS0246 (undefined type) - the MutationApplier tests assert that
// this PRE-EXISTING baseline error never blocks an unrelated valid edit elsewhere in
// the project, since only NEW errors relative to the load-time baseline are rejected.
public class Broken
{
    public UndefinedType? Field;
}
