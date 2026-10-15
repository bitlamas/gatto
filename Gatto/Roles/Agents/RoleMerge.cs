namespace Gatto.Roles.Agents;

public static class RoleMerge
{
    //a called role takes the caller's model, endpoint and thinking where it sets none, and raw from either side, since a bool cannot say unset
    public static RoleFile ForChild(RoleFile called, RoleFile caller) => called with
    {
        Model = called.Model ?? caller.Model,
        Endpoint = called.Endpoint ?? caller.Endpoint,
        ThinkingRequested = called.ThinkingRequested ?? caller.ThinkingRequested,
        Raw = called.Raw || caller.Raw,
    };
}
