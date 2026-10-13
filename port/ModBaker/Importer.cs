using System;
using System.Linq;
using Mono.Cecil;

namespace EtgModBaker;

/// <summary>
/// Imports references into one module, mapping them onto the game's assemblies. Member references are
/// built by hand: Cecil's importer would copy a foreign member's signature types as they are, e.g. a
/// mod's reference to Assembly-CSharp's PlayerController imported into Assembly-CSharp itself.
/// </summary>
internal sealed class Importer
{
    private readonly CecilWorld _world;
    private readonly MethodDefinition _context;
    private readonly ModuleDefinition _module;

    public Importer(CecilWorld world, MethodDefinition context)
    {
        _world = world;
        _context = context;
        _module = context.Module;
    }

    public Importer(CecilWorld world, ModuleDefinition module)
    {
        _world = world;
        _module = module;
    }

    // Generic parameters inside a body refer to the method's / declaring type's own.
    public TypeReference Type(TypeReference t) => Map(t, gp =>
    {
        if (_context == null) throw new InvalidOperationException("generic parameter outside a method context: " + gp.FullName);
        return gp.Type == GenericParameterType.Method ? _context.GenericParameters[gp.Position] : _context.DeclaringType.GenericParameters[gp.Position];
    });

    private TypeReference Map(TypeReference t, Func<GenericParameter, TypeReference> gpMap)
    {
        switch (t)
        {
            case GenericParameter gp:
                return gpMap(gp);
            case ArrayType a:
            {
                var na = new ArrayType(Map(a.ElementType, gpMap), a.Rank);
                if (a.Rank > 1)
                {
                    na.Dimensions.Clear();
                    foreach (var d in a.Dimensions) na.Dimensions.Add(new ArrayDimension(d.LowerBound, d.UpperBound));
                }
                return na;
            }
            case ByReferenceType b: return new ByReferenceType(Map(b.ElementType, gpMap));
            case PointerType p: return new PointerType(Map(p.ElementType, gpMap));
            case PinnedType p: return new PinnedType(Map(p.ElementType, gpMap));
            case SentinelType s: return new SentinelType(Map(s.ElementType, gpMap));
            case RequiredModifierType r: return new RequiredModifierType(Map(r.ModifierType, gpMap), Map(r.ElementType, gpMap));
            case OptionalModifierType o: return new OptionalModifierType(Map(o.ModifierType, gpMap), Map(o.ElementType, gpMap));
            case GenericInstanceType g:
            {
                var ng = new GenericInstanceType(Map(g.ElementType, gpMap));
                foreach (var arg in g.GenericArguments) ng.GenericArguments.Add(Map(arg, gpMap));
                return ng;
            }
            case TypeSpecification spec:
                throw new NotSupportedException("type spec " + spec.GetType().Name);
        }
        return Leaf(t);
    }

    private TypeReference Leaf(TypeReference t)
    {
        var def = _world.FindType(t) ?? throw new InvalidOperationException("type not found in game/mod assemblies: " + t.FullName + " (" + t.Scope + ")");
        if (def.Module == _module) return def;
        int refs = _module.AssemblyReferences.Count;
        var r = _module.ImportReference(def);
        // One reference per assembly name: the game ships one 0Harmony/Newtonsoft..., whatever the
        // version a mod was built against.
        var outer = r;
        while (outer.DeclaringType != null) outer = outer.DeclaringType;
        if (outer.Scope is AssemblyNameReference added)
        {
            var existing = _module.AssemblyReferences.FirstOrDefault(a => a.Name == added.Name && a != added);
            if (existing != null)
            {
                outer.Scope = existing;
                if (_module.AssemblyReferences.Count > refs) _module.AssemblyReferences.Remove(added);
            }
        }
        return r;
    }

    public MethodReference Method(MethodReference m)
    {
        if (m is GenericInstanceMethod gim)
        {
            var ng = new GenericInstanceMethod(Method(gim.ElementMethod));
            foreach (var arg in gim.GenericArguments) ng.GenericArguments.Add(Type(arg));
            return ng;
        }
        var declaring = Type(m.DeclaringType);
        var declDef = _world.FindType(m.DeclaringType.GetElementType())
            ?? throw new InvalidOperationException("declaring type not found: " + m.FullName);
        string sig = Sig(m);
        var def = declDef.Methods.FirstOrDefault(md => md.Name == m.Name && Sig(md) == sig)
            ?? throw new InvalidOperationException($"method not found: {m.FullName} in {declDef.Module.Assembly.Name.Name}");
        if (def.Module == _module && declaring == def.DeclaringType) return def;
        var r = new MethodReference(def.Name, _module.TypeSystem.Void, declaring)
        {
            HasThis = def.HasThis,
            ExplicitThis = def.ExplicitThis,
            CallingConvention = def.CallingConvention,
        };
        foreach (var gp in def.GenericParameters) r.GenericParameters.Add(new GenericParameter(gp.Name, r));
        // Signatures speak of !0 / !!0, not of concrete generic parameters.
        Func<GenericParameter, TypeReference> sigMap = gp => gp.Type == GenericParameterType.Method
            ? r.GenericParameters[gp.Position]
            : declaring.GetElementType().GenericParameters[gp.Position];
        r.ReturnType = Map(def.ReturnType, sigMap);
        foreach (var p in def.Parameters) r.Parameters.Add(new ParameterDefinition(Map(p.ParameterType, sigMap)));
        return r;
    }

    public FieldReference Field(FieldReference f)
    {
        var declaring = Type(f.DeclaringType);
        var declDef = _world.FindType(f.DeclaringType.GetElementType())
            ?? throw new InvalidOperationException("declaring type not found: " + f.FullName);
        var def = declDef.Fields.FirstOrDefault(fd => fd.Name == f.Name)
            ?? throw new InvalidOperationException("field not found: " + f.FullName);
        if (def.Module == _module && declaring == def.DeclaringType) return def;
        return new FieldReference(def.Name, Map(def.FieldType, gp => declaring.GetElementType().GenericParameters[gp.Position]), declaring);
    }

    // Signature key that's the same for a definition and for a reference to it from another module.
    internal static string Sig(IMethodSignature m)
    {
        int generics = m is MethodReference mr ? mr.GenericParameters.Count : 0;
        return $"{generics}|{TypeKey(m.ReturnType)}({string.Join(",", m.Parameters.Select(p => TypeKey(p.ParameterType)))})";
    }

    private static string TypeKey(TypeReference t) => t switch
    {
        GenericParameter gp => (gp.Type == GenericParameterType.Method ? "!!" : "!") + gp.Position,
        ArrayType a => TypeKey(a.ElementType) + "[" + new string(',', a.Rank - 1) + "]",
        ByReferenceType b => TypeKey(b.ElementType) + "&",
        PointerType p => TypeKey(p.ElementType) + "*",
        PinnedType p => TypeKey(p.ElementType),
        RequiredModifierType r => TypeKey(r.ElementType) + " modreq(" + TypeKey(r.ModifierType) + ")",
        OptionalModifierType o => TypeKey(o.ElementType) + " modopt(" + TypeKey(o.ModifierType) + ")",
        GenericInstanceType g => TypeKey(g.ElementType) + "<" + string.Join(",", g.GenericArguments.Select(TypeKey)) + ">",
        _ => t.DeclaringType != null ? TypeKey(t.DeclaringType) + "/" + t.Name : t.FullName,
    };
}
