﻿using System.Linq;
using Fody;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Mono.Collections.Generic;

public static class GetHashCodeInjector
{
    const string customAttribute = "CustomGetHashCodeAttribute";

    const int magicNumber = 397;

    public static void InjectGetHashCode(TypeDefinition type, bool ignoreBaseClassProperties)
    {
        var methodAttributes = MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.Virtual;
        var method = new MethodDefinition("GetHashCode", methodAttributes, ModuleWeaver.Int32Type);
        method.CustomAttributes.MarkAsGeneratedCode();

        var resultVariable = method.Body.Variables.Add(ModuleWeaver.Int32Type);

        var body = method.Body;
        body.InitLocals = true;
        var ins = body.Instructions;

        ins.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        ins.Add(Instruction.Create(OpCodes.Stloc, resultVariable));

        var properties = ModuleWeaver.ImportCustom(type).Resolve().GetPropertiesWithoutIgnores(ModuleWeaver.ignoreAttributeName);
        if (ignoreBaseClassProperties)
        {
            properties = properties.IgnoreBaseClassProperties(type);
        }

        if (properties.Length == 0)
        {
            AddResultInit(ins, resultVariable);
        }

        var methods = type.GetMethods();
        var customLogic = methods
            .Where(x => x.CustomAttributes.Any(y => y.AttributeType.Name == customAttribute)).ToArray();

        if (customLogic.Length > 2)
        {
            throw new WeavingException("Only one custom method can be specified.");
        }

        var isFirst = true;
        foreach (var property in properties)
        {
            var variable = AddPropertyCode(property, isFirst, ins, resultVariable, method, type);

            if (variable != null)
            {
                method.Body.Variables.Add(variable);
            }

            isFirst = false;
        }

        if (customLogic.Length == 1)
        {
            ins.Add(Instruction.Create(OpCodes.Ldloc, resultVariable));
            ins.Add(Instruction.Create(OpCodes.Ldc_I4, magicNumber));
            ins.Add(Instruction.Create(OpCodes.Mul));
            AddCustomLogicCall(type, ins, customLogic[0]);
            ins.Add(Instruction.Create(OpCodes.Xor));
            ins.Add(Instruction.Create(OpCodes.Stloc, resultVariable));
        }

        AddReturnCode(ins, resultVariable);

        body.OptimizeMacros();

        type.Methods.AddOrReplace(method);
    }

    static void AddCustomLogicCall(TypeDefinition type, Collection<Instruction> ins, MethodDefinition customLogic)
    {
        var customMethod = ModuleWeaver.ImportCustom(customLogic);

        var parameters = customMethod.Parameters;
        if (parameters.Count != 0)
        {
            throw new WeavingException($"Custom GetHashCode of type {type.FullName} have to have empty parameter list.");
        }
        if (customMethod.ReturnType.FullName != typeof(int).FullName)
        {
            throw new WeavingException($"Custom GetHashCode of type {type.FullName} have to return int.");
        }

        MethodReference selectedMethod;
        if (customMethod.DeclaringType.HasGenericParameters)
        {
            var genericInstanceType = type.GetGenericInstanceType(type);
            var resolverType = ModuleWeaver.ImportCustom(genericInstanceType);
            var newRef = new MethodReference(customMethod.Name, customMethod.ReturnType)
            {
                DeclaringType = resolverType,
                HasThis = true,
            };

            selectedMethod = newRef;
        }
        else
        {
            selectedMethod = customMethod;
        }

        var imported = ModuleWeaver.ImportCustom(selectedMethod);
        ins.Add(Instruction.Create(OpCodes.Ldarg_0));
        ins.Add(Instruction.Create(imported.GetCallForMethod(), imported));
    }

    static void AddReturnCode(Collection<Instruction> ins, VariableDefinition resultVariable)
    {
        ins.Add(Instruction.Create(OpCodes.Ldloc, resultVariable));
        ins.Add(Instruction.Create(OpCodes.Ret));
    }

    static VariableDefinition AddPropertyCode(PropertyDefinition property, bool isFirst, Collection<Instruction> ins, VariableDefinition resultVariable, MethodDefinition method, TypeDefinition type)
    {
        VariableDefinition variable = null;
        bool isCollection;
        var propType = ModuleWeaver.ImportCustom(property.PropertyType.GetGenericInstanceType(type));
        if (property.PropertyType.IsGenericParameter)
        {
            isCollection = false;
        }
        else
        {
            isCollection = propType.Resolve().IsCollection() || property.PropertyType.IsArray;
        }

        AddMultiplicityByMagicNumber(isFirst, ins, resultVariable, isCollection);

        if (property.PropertyType.FullName.StartsWith("System.Nullable`1"))
        {
            variable = AddNullableProperty(property, ins, type, variable);
        }
        else if (isCollection && property.PropertyType.FullName != "System.String")
        {
            AddCollectionCode(property, ins, resultVariable, method, type);
        }
        else if (property.PropertyType.IsValueType || property.PropertyType.IsGenericParameter)
        {
            LoadVariable(property, ins, type);
            if (property.PropertyType.FullName != "System.Int32")
            {
                ins.Add(Instruction.Create(OpCodes.Box, propType));
                ins.Add(Instruction.Create(OpCodes.Callvirt, ModuleWeaver.GetHashcode));
            }
        }
        else
        {
            LoadVariable(property, ins, type);
            AddNormalCode(property, ins, type);
        }

        if (!isFirst && !isCollection)
        {
            ins.Add(Instruction.Create(OpCodes.Xor));
        }

        if (!isCollection)
        {
            ins.Add(Instruction.Create(OpCodes.Stloc, resultVariable));
        }

        return variable;
    }

    static VariableDefinition AddNullableProperty(PropertyDefinition property, Collection<Instruction> ins, TypeDefinition type, VariableDefinition variable)
    {
        ins.If(c =>
            {
                var nullablePropertyResolved = property.PropertyType.Resolve();
                var nullablePropertyImported = ModuleWeaver.ImportCustom(property.PropertyType);

                ins.Add(Instruction.Create(OpCodes.Ldarg_0));
                var getMethod = ModuleWeaver.ImportCustom(property.GetGetMethod(type));
                c.Add(Instruction.Create(OpCodes.Call, getMethod));

                variable = new VariableDefinition(getMethod.ReturnType);
                c.Add(Instruction.Create(OpCodes.Stloc, variable));
                c.Add(Instruction.Create(OpCodes.Ldloca, variable));

                var hasValuePropertyResolved = nullablePropertyResolved.Properties.First(x => x.Name == "HasValue").Resolve();
                var hasMethod = ModuleWeaver.ImportCustom(hasValuePropertyResolved.GetGetMethod(nullablePropertyImported));
                c.Add(Instruction.Create(OpCodes.Call, hasMethod));
            },
            t =>
            {
                var nullableProperty = ModuleWeaver.ImportCustom(property.PropertyType);

                t.Add(Instruction.Create(OpCodes.Ldarg_0));
                var imp = property.GetGetMethod(type);
                var imp2 = ModuleWeaver.ImportCustom(imp);

                t.Add(Instruction.Create(OpCodes.Call, imp2));
                t.Add(Instruction.Create(OpCodes.Box, nullableProperty));
                t.Add(Instruction.Create(OpCodes.Callvirt, ModuleWeaver.GetHashcode));
            },
            e => e.Add(Instruction.Create(OpCodes.Ldc_I4_0)));
        return variable;
    }

    static void LoadVariable(PropertyDefinition property, Collection<Instruction> ins, TypeDefinition type)
    {
        var get = property.GetGetMethod(type);
        var imported = ModuleWeaver.ImportCustom(get);
        ins.Add(Instruction.Create(OpCodes.Ldarg_0));
        ins.Add(Instruction.Create(OpCodes.Call, imported));
    }

    static void AddMultiplicityByMagicNumber(bool isFirst, Collection<Instruction> ins, VariableDefinition resultVariable,
        bool isCollection)
    {
        if (!isFirst && !isCollection)
        {
            ins.Add(Instruction.Create(OpCodes.Ldloc, resultVariable));
            ins.Add(Instruction.Create(OpCodes.Ldc_I4, magicNumber));
            ins.Add(Instruction.Create(OpCodes.Mul));
        }
    }

    static void AddNormalCode(PropertyDefinition property, Collection<Instruction> ins, TypeDefinition type)
    {
        ins.If(
            c =>
            {
            },
            t =>
            {
                LoadVariable(property, t, type);
                t.Add(Instruction.Create(OpCodes.Callvirt, ModuleWeaver.GetHashcode));
            },
            f => f.Add(Instruction.Create(OpCodes.Ldc_I4_0)));
    }

    static void AddCollectionCode(PropertyDefinition property, Collection<Instruction> ins, VariableDefinition resultVariable, MethodDefinition method, TypeDefinition type)
    {
        if (!property.PropertyType.IsValueType)
        {
            ins.If(
                c => LoadVariable(property, c, type),
                t =>
                {
                    GenerateCollectionCode(property, resultVariable, method, type, t);
                },
                f => { });
        }
        else
        {
            GenerateCollectionCode(property, resultVariable, method, type, ins);
        }
    }

    static void GenerateCollectionCode(PropertyDefinition property, VariableDefinition resultVariable,
        MethodDefinition method, TypeDefinition type, Collection<Instruction> t)
    {
        LoadVariable(property, t, type);
        var enumeratorVariable = method.Body.Variables.Add(ModuleWeaver.IEnumeratorType);
        var currentVariable = method.Body.Variables.Add(ModuleWeaver.ObjectType);

        AddGetEnumerator(t, enumeratorVariable, property);

        AddCollectionLoop(resultVariable, t, enumeratorVariable, currentVariable);
    }

    static void AddCollectionLoop(VariableDefinition resultVariable, Collection<Instruction> t, VariableDefinition enumeratorVariable, VariableDefinition currentVariable)
    {
        t.While(
            c =>
            {
                c.Add(Instruction.Create(OpCodes.Ldloc, enumeratorVariable));
                c.Add(Instruction.Create(OpCodes.Callvirt, ModuleWeaver.MoveNext));
            },
            b =>
            {
                b.Add(Instruction.Create(OpCodes.Ldloc, resultVariable));
                b.Add(Instruction.Create(OpCodes.Ldc_I4, magicNumber));
                b.Add(Instruction.Create(OpCodes.Mul));

                b.Add(Instruction.Create(OpCodes.Ldloc, enumeratorVariable));
                b.Add(Instruction.Create(OpCodes.Callvirt, ModuleWeaver.GetCurrent));
                b.Add(Instruction.Create(OpCodes.Stloc, currentVariable));

                b.If(
                    bc => b.Add(Instruction.Create(OpCodes.Ldloc, currentVariable)),
                    bt =>
                    {
                        bt.Add(Instruction.Create(OpCodes.Ldloc, currentVariable));
                        bt.Add(Instruction.Create(OpCodes.Callvirt, ModuleWeaver.GetHashcode));
                    },
                    et => et.Add(Instruction.Create(OpCodes.Ldc_I4_0)));
                b.Add(Instruction.Create(OpCodes.Xor));
                b.Add(Instruction.Create(OpCodes.Stloc, resultVariable));
            });
    }

    static void AddResultInit(Collection<Instruction> ins, VariableDefinition resultVariable)
    {
        ins.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        ins.Add(Instruction.Create(OpCodes.Stloc, resultVariable));
    }

    static void AddGetEnumerator(Collection<Instruction> ins, VariableDefinition variable, PropertyDefinition property)
    {
        if (property.PropertyType.IsValueType)
        {
            var imported = ModuleWeaver.ImportCustom(property.PropertyType);
            ins.Add(Instruction.Create(OpCodes.Box, imported));
        }

        ins.Add(Instruction.Create(OpCodes.Callvirt, ModuleWeaver.GetEnumerator));
        ins.Add(Instruction.Create(OpCodes.Stloc, variable));
    }
}