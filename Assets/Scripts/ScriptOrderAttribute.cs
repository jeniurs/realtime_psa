using System;
using UnityEngine;

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class ScriptOrderAttribute : Attribute
{
    public int Order { get; private set; }

    public ScriptOrderAttribute(int order)
    {
        Order = order;
    }
}
