﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Data;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace fastJSON
{
	internal delegate object CreateObject ();

	/// <summary>
	/// The cached serialization infomation used by the reflection engine during serialization and deserialization.
	/// </summary>
	internal sealed class SerializationManager
	{
		private static readonly char[] __enumSeperatorCharArray = { ',' };

		private readonly SafeDictionary<Type, DefinitionCache> _memberDefinitions = new SafeDictionary<Type, DefinitionCache> ();
		private readonly IReflectionController _controller;
		internal readonly SafeDictionary<Enum, string> EnumValueCache = new SafeDictionary<Enum, string> ();

		public IReflectionController ReflectionController { get { return _controller; } }

		/// <summary>
		/// Gets the singleton instance.
		/// </summary>
		public static readonly SerializationManager Instance = new SerializationManager (new DefaultReflectionController ());

		public SerializationManager (IReflectionController provider) {
			_controller = provider;
		}

		internal DefinitionCache GetDefinition (Type type) {
			DefinitionCache c;
			if (_memberDefinitions.TryGetValue (type, out c)) {
				return c;
			}
			bool skip = false;
			c = Register (type);
			if (c.AlwaysDeserializable == false) {
				if (type.IsGenericType || type.IsArray) {
					skip = ShouldSkipVisibilityCheck (type);
				}
			}
			c.Constructor = CreateConstructorMethod (type, skip | c.AlwaysDeserializable);
			_memberDefinitions[type] = c;
			return c;
		}

		private bool ShouldSkipVisibilityCheck (Type type) {
			DefinitionCache c;
			if (type.IsGenericType) {
				var pl = Reflection.Instance.GetGenericArguments (type);
				foreach (var t in pl) {
					if (_memberDefinitions.TryGetValue (t, out c) == false) {
						c = Register (t);
					}
					if (c.AlwaysDeserializable) {
						return true;
					}
					if (t.IsGenericType || t.IsArray) {
						if (ShouldSkipVisibilityCheck (t)) {
							return true;
						}
					}
				}
			}
			if (type.IsArray) {
				var t = type.GetElementType ();
				if (_memberDefinitions.TryGetValue (t, out c) == false) {
					c = Register (t);
				}
				if (c.AlwaysDeserializable) {
					return true;
				}
			}
			return false;
		}

		private DefinitionCache Register<T> () { return Register (typeof (T)); }
		private DefinitionCache Register (Type type) {
			return new DefinitionCache (type, (IReflectionController)_controller);
		}

		private static CreateObject CreateConstructorMethod (Type objtype, bool skipVisibility) {
			CreateObject c;
			var n = objtype.Name + ".ctor";
			if (objtype.IsClass) {
				DynamicMethod dynMethod = skipVisibility ? new DynamicMethod (n, objtype, null, objtype, true) : new DynamicMethod (n, objtype, Type.EmptyTypes);
				ILGenerator ilGen = dynMethod.GetILGenerator ();
				var ct = objtype.GetConstructor (Type.EmptyTypes);
				if (ct == null) {
					return null;
				}
				ilGen.Emit (OpCodes.Newobj, ct);
				ilGen.Emit (OpCodes.Ret);
				c = (CreateObject)dynMethod.CreateDelegate (typeof (CreateObject));
			}
			else {// structs
				DynamicMethod dynMethod = skipVisibility ? new DynamicMethod (n, typeof (object), null, objtype, true) : new DynamicMethod (n, typeof (object), null, objtype);
				ILGenerator ilGen = dynMethod.GetILGenerator ();
				var lv = ilGen.DeclareLocal (objtype);
				ilGen.Emit (OpCodes.Ldloca_S, lv);
				ilGen.Emit (OpCodes.Initobj, objtype);
				ilGen.Emit (OpCodes.Ldloc_0);
				ilGen.Emit (OpCodes.Box, objtype);
				ilGen.Emit (OpCodes.Ret);
				c = (CreateObject)dynMethod.CreateDelegate (typeof (CreateObject));
			}
			return c;
		}

		public string GetEnumName (Enum v) {
			string t;
			if (EnumValueCache.TryGetValue (v, out t)) {
				return t;
			}
			var et = v.GetType ();
			var f = GetDefinition (et);
			if (EnumValueCache.TryGetValue (v, out t)) {
				return t;
			}
			if (f.IsFlaggedEnum) {
				var vs = Enum.GetValues (et);
				var iv = (ulong)Convert.ToInt64 (v);
				var ov = iv;
				if (iv == 0) {
					return "0"; // should not be here
				}
				var sl = new List<string> ();
				var vm = f.EnumNames;
				for (int i = vs.Length - 1; i > 0; i--) {
					var ev = (ulong)Convert.ToInt64 (vs.GetValue (i));
					if (ev == 0) {
						continue;
					}
					if ((iv & ev) == ev) {
						iv -= ev;
						sl.Add (EnumValueCache[(Enum)Enum.ToObject (et, ev)]);
					}
				}
				if (iv != 0) {
					return null;
				}
				sl.Reverse ();
				t = String.Join (",", sl.ToArray ());
				EnumValueCache.Add (v, t);
			}
			return t;
		}

		public Enum GetEnumValue (Type type, string name) {
			var def = GetDefinition (type);
			Enum e;
			if (def.EnumNames.TryGetValue (name, out e)) {
				return e;
			}
			if (def.IsFlaggedEnum) {
				ulong v = 0;
				var s = name.Split (__enumSeperatorCharArray);
				foreach (var item in s) {
					if (def.EnumNames.TryGetValue (item, out e) == false) {
						throw new KeyNotFoundException ("Key \"" + item + "\" not found for type " + type.FullName);
					}
					v |= Convert.ToUInt64 (e);
				}
				return (Enum)Enum.ToObject (type, v);
			}
			throw new KeyNotFoundException ("Key \"" + name + "\" not found for type " + type.FullName);
		}
	}

	class DefinitionCache
	{
		internal delegate object GenericSetter (object target, object value);
		internal delegate object GenericGetter (object obj);

		public readonly string TypeName;
		public readonly string AssemblyName;

		public readonly bool AlwaysDeserializable;
		internal CreateObject Constructor;
		public readonly IJsonInterceptor Interceptor;
		public readonly Getters[] Getters;
		public readonly Dictionary<string, myPropInfo> Properties;

		public readonly bool IsFlaggedEnum;
		public readonly Dictionary<string, Enum> EnumNames;

		public DefinitionCache (Type type, IReflectionController controller) {
			TypeName = type.FullName;
			AssemblyName = type.AssemblyQualifiedName;
			if (type.IsEnum) {
				IsFlaggedEnum = AttributeHelper.GetAttribute<FlagsAttribute> (type, false) != null;
				EnumNames = GetEnumValues (type, controller);
				return;
			}

			if (controller != null) {
				AlwaysDeserializable = controller.IsAlwaysDeserializable (type);
				Interceptor = controller.GetInterceptor (type);
			}
			if (typeof (IEnumerable).IsAssignableFrom (type)) {
				return;
			}
			Getters = GetGetters (type, controller);
			Properties = GetProperties (type, controller);
		}

		public object Instantiate () {
			if (Constructor != null) {
				try {
					return Constructor ();
				}
				catch (Exception ex) {
					throw new JsonSerializationException(string.Format("Failed to fast create instance for type '{0}' from assembly '{1}'", TypeName, AssemblyName), ex);
				}
			}
			return null;
		}

		#region Accessor methods
		private static Getters[] GetGetters (Type type, IReflectionController controller) {
			PropertyInfo[] props = type.GetProperties (BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
			FieldInfo[] fi = type.GetFields (BindingFlags.Instance | BindingFlags.Public | BindingFlags.Static);
			Dictionary<string, Getters> getters = new Dictionary<string, Getters> (props.Length + fi.Length);

			foreach (PropertyInfo p in props) {
				if (p.GetIndexParameters ().Length > 0) {// Property is an indexer
					continue;
				}
				AddGetter (getters, p, CreateGetProperty (type, p), controller);
			}

			foreach (var f in fi) {
				if (f.IsLiteral == false) {
					AddGetter (getters, f, CreateGetField (type, f), controller);
				}
			}

			var r = new Getters[getters.Count];
			getters.Values.CopyTo (r, 0);
			return r;
		}

		private static void AddGetter (Dictionary<string, Getters> getters, MemberInfo memberInfo, GenericGetter getter, IReflectionController controller) {
			var n = memberInfo.Name;
			bool s; // static
			bool ro; // readonly
			Type t; // member type
			bool tp; // property
			if (memberInfo is FieldInfo) {
				var f = ((FieldInfo)memberInfo);
				s = f.IsStatic;
				ro = f.IsInitOnly;
				t = f.FieldType;
				tp = false;
			}
			else { // PropertyInfo
				var p = ((PropertyInfo)memberInfo);
				s = (p.GetGetMethod () ?? p.GetSetMethod ()).IsStatic;
				ro = p.GetSetMethod () == null; // p.CanWrite can return true if the setter is non-public
				t = p.PropertyType;
				tp = true;
			}
			bool? ms = null;
			if (controller != null) {
				ms = controller.IsMemberSerializable (memberInfo, tp, ro, s);
				if (ms.HasValue && ms.Value == false) {
					return;
				}
			}
			var g = new Getters {
				Getter = getter,
				Name = n,
				IsStatic = s,
				IsProperty = tp,
				IsReadOnly = ro,
				MemberType = t
			};
			if (controller != null) {
				object dv;
				if (ms.HasValue) {
					g.AlwaysInclude = ms.Value;
				}
				g.Converter = controller.GetMemberConverter (memberInfo);
				g.HasDefaultValue = controller.GetDefaultValue (memberInfo, out dv);
				if (g.HasDefaultValue) {
					g.DefaultValue = dv;
				}
				var tn = controller.GetSerializedNames (memberInfo);
				if (tn != null) {
					if (String.IsNullOrEmpty (tn.DefaultName) == false && tn.DefaultName != g.Name) {
						g.SpecificName = true;
					}
					g.Name = tn.DefaultName ?? g.Name;
					if (tn.Count > 0) {
						g.TypedNames = new Dictionary<Type, string> (tn);
						g.SpecificName = true;
					}
				}
			}
			getters.Add (n, g);
		}

		private static GenericGetter CreateGetField (Type type, FieldInfo fieldInfo) {
			DynamicMethod dynamicGet = new DynamicMethod (fieldInfo.Name, typeof (object), new Type[] { typeof (object) }, type, true);

			ILGenerator il = dynamicGet.GetILGenerator ();

			if (!type.IsClass) // structs
			{
				var lv = il.DeclareLocal (type);
				il.Emit (OpCodes.Ldarg_0);
				il.Emit (OpCodes.Unbox_Any, type);
				il.Emit (OpCodes.Stloc_0);
				il.Emit (OpCodes.Ldloca_S, lv);
				il.Emit (OpCodes.Ldfld, fieldInfo);
				if (fieldInfo.FieldType.IsValueType)
					il.Emit (OpCodes.Box, fieldInfo.FieldType);
			}
			else {
				il.Emit (OpCodes.Ldarg_0);
				il.Emit (OpCodes.Ldfld, fieldInfo);
				if (fieldInfo.FieldType.IsValueType)
					il.Emit (OpCodes.Box, fieldInfo.FieldType);
			}

			il.Emit (OpCodes.Ret);

			return (GenericGetter)dynamicGet.CreateDelegate (typeof (GenericGetter));
		}

		private static GenericGetter CreateGetProperty (Type type, PropertyInfo propertyInfo) {
			MethodInfo getMethod = propertyInfo.GetGetMethod ();
			if (getMethod == null)
				return null;

			DynamicMethod getter = new DynamicMethod (getMethod.Name, typeof (object), new Type[] { typeof (object) }, type, true);

			ILGenerator il = getter.GetILGenerator ();

			if (!type.IsClass) // structs
			{
				var lv = il.DeclareLocal (type);
				il.Emit (OpCodes.Ldarg_0);
				il.Emit (OpCodes.Unbox_Any, type);
				il.Emit (OpCodes.Stloc_0);
				il.Emit (OpCodes.Ldloca_S, lv);
				il.EmitCall (OpCodes.Call, getMethod, null);
				if (propertyInfo.PropertyType.IsValueType)
					il.Emit (OpCodes.Box, propertyInfo.PropertyType);
			}
			else {
				if (getMethod.IsStatic) {
					il.EmitCall (OpCodes.Call, getMethod, null);
				}
				else {
					il.Emit (OpCodes.Ldarg_0);
					il.Emit (OpCodes.Castclass, propertyInfo.DeclaringType);
					il.EmitCall (OpCodes.Callvirt, getMethod, null);
				}
				if (propertyInfo.PropertyType.IsValueType)
					il.Emit (OpCodes.Box, propertyInfo.PropertyType);
			}

			il.Emit (OpCodes.Ret);

			return (GenericGetter)getter.CreateDelegate (typeof (GenericGetter));
		}

		private static GenericSetter CreateSetField (Type type, FieldInfo fieldInfo) {
			Type[] arguments = new Type[2];
			arguments[0] = arguments[1] = typeof (object);

			DynamicMethod dynamicSet = new DynamicMethod (fieldInfo.Name, typeof (object), arguments, type, true);

			ILGenerator il = dynamicSet.GetILGenerator ();

			if (!type.IsClass) // structs
			{
				var lv = il.DeclareLocal (type);
				il.Emit (OpCodes.Ldarg_0);
				il.Emit (OpCodes.Unbox_Any, type);
				il.Emit (OpCodes.Stloc_0);
				il.Emit (OpCodes.Ldloca_S, lv);
				il.Emit (OpCodes.Ldarg_1);
				if (fieldInfo.FieldType.IsClass)
					il.Emit (OpCodes.Castclass, fieldInfo.FieldType);
				else
					il.Emit (OpCodes.Unbox_Any, fieldInfo.FieldType);
				il.Emit (OpCodes.Stfld, fieldInfo);
				il.Emit (OpCodes.Ldloc_0);
				il.Emit (OpCodes.Box, type);
				il.Emit (OpCodes.Ret);
			}
			else {
				il.Emit (OpCodes.Ldarg_0);
				il.Emit (OpCodes.Ldarg_1);
				if (fieldInfo.FieldType.IsValueType)
					il.Emit (OpCodes.Unbox_Any, fieldInfo.FieldType);
				il.Emit (OpCodes.Stfld, fieldInfo);
				il.Emit (OpCodes.Ldarg_0);
				il.Emit (OpCodes.Ret);
			}
			return (GenericSetter)dynamicSet.CreateDelegate (typeof (GenericSetter));
		}

		private static GenericSetter CreateSetMethod (Type type, PropertyInfo propertyInfo) {
			MethodInfo setMethod = propertyInfo.GetSetMethod ();
			if (setMethod == null)
				return null;

			Type[] arguments = new Type[2];
			arguments[0] = arguments[1] = typeof (object);

			DynamicMethod setter = new DynamicMethod (setMethod.Name, typeof (object), arguments, true);
			ILGenerator il = setter.GetILGenerator ();

			if (!type.IsClass) // structs
			{
				var lv = il.DeclareLocal (type);
				il.Emit (OpCodes.Ldarg_0);
				il.Emit (OpCodes.Unbox_Any, type);
				il.Emit (OpCodes.Stloc_0);
				il.Emit (OpCodes.Ldloca_S, lv);
				il.Emit (OpCodes.Ldarg_1);
				if (propertyInfo.PropertyType.IsClass)
					il.Emit (OpCodes.Castclass, propertyInfo.PropertyType);
				else
					il.Emit (OpCodes.Unbox_Any, propertyInfo.PropertyType);
				il.EmitCall (OpCodes.Call, setMethod, null);
				il.Emit (OpCodes.Ldloc_0);
				il.Emit (OpCodes.Box, type);
			}
			else {
				if (setMethod.IsStatic) {
					il.Emit (OpCodes.Ldarg_1);
					if (propertyInfo.PropertyType.IsClass)
						il.Emit (OpCodes.Castclass, propertyInfo.PropertyType);
					else
						il.Emit (OpCodes.Unbox_Any, propertyInfo.PropertyType);
					il.EmitCall (OpCodes.Call, setMethod, null);
				}
				else {
					il.Emit (OpCodes.Ldarg_0);
					il.Emit (OpCodes.Castclass, propertyInfo.DeclaringType);
					il.Emit (OpCodes.Ldarg_1);
					if (propertyInfo.PropertyType.IsClass)
						il.Emit (OpCodes.Castclass, propertyInfo.PropertyType);
					else
						il.Emit (OpCodes.Unbox_Any, propertyInfo.PropertyType);
					il.EmitCall (OpCodes.Callvirt, setMethod, null);
				}
				il.Emit (OpCodes.Ldarg_0);
			}

			il.Emit (OpCodes.Ret);

			return (GenericSetter)setter.CreateDelegate (typeof (GenericSetter));
		}

		private static Dictionary<string, myPropInfo> GetProperties (Type type, IReflectionController controller) {
			bool custType = Reflection.Instance.IsTypeRegistered (type);
			Dictionary<string, myPropInfo> sd = new Dictionary<string, myPropInfo> (StringComparer.OrdinalIgnoreCase);
			PropertyInfo[] pr = type.GetProperties (BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
			foreach (PropertyInfo p in pr) {
				if (p.GetIndexParameters ().Length > 0) {// Property is an indexer
					continue;
				}
				myPropInfo d = CreateMyProp (p.PropertyType, p.Name, custType);
				d.Setter = CreateSetMethod (type, p);
				if (d.Setter != null)
					d.CanWrite = true;
				d.Getter = CreateGetProperty (type, p);
				AddMyPropInfo (sd, d, p, controller);
			}
			FieldInfo[] fi = type.GetFields (BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
			foreach (FieldInfo f in fi) {
				if (f.IsLiteral || f.IsInitOnly) {
					continue;
				}
				myPropInfo d = CreateMyProp (f.FieldType, f.Name, custType);
				//if (f.IsInitOnly == false) {
				d.Setter = CreateSetField (type, f);
				if (d.Setter != null)
					d.CanWrite = true;
				//}
				d.Getter = CreateGetField (type, f);
				AddMyPropInfo (sd, d, f, controller);
			}

			return sd;
		}

		private static void AddMyPropInfo (Dictionary<string, myPropInfo> sd, myPropInfo d, MemberInfo member, IReflectionController controller) {
			if (controller != null) {
				if (controller.IsMemberDeserializable (member) == false) {
					d.Setter = null;
					d.CanWrite = false;
					return;
				}
				d.Converter = controller.GetMemberConverter (member);
				var tn = controller.GetSerializedNames (member);
				if (tn != null) {
					if (String.IsNullOrEmpty (tn.DefaultName) == false) {
						d.Name = tn.DefaultName;
					}
					foreach (var item in tn) {
						var st = item.Key;
						var sn = item.Value;
						var dt = CreateMyProp (st, sn, Reflection.Instance.IsTypeRegistered (st));
						dt.Getter = d.Getter;
						dt.Setter = d.Setter;
						dt.Converter = d.Converter;
						dt.CanWrite = d.CanWrite;
						sd.Add (sn, dt);
					}
				}
			}
			sd.Add (d.Name, d);
		}

		private static myPropInfo CreateMyProp (Type type, string name, bool customType) {
			myPropInfo d = new myPropInfo ();
			myPropInfoType d_type = myPropInfoType.Unknown;

			if (type == typeof (int) || type == typeof (int?)) d_type = myPropInfoType.Int;
			else if (type == typeof (long) || type == typeof (long?)) d_type = myPropInfoType.Long;
			else if (type == typeof (string)) d_type = myPropInfoType.String;
			else if (type == typeof (bool) || type == typeof (bool?)) d_type = myPropInfoType.Bool;
			else if (type == typeof (DateTime) || type == typeof (DateTime?)) d_type = myPropInfoType.DateTime;
			else if (type.IsEnum) d_type = myPropInfoType.Enum;
			else if (type == typeof (Guid) || type == typeof (Guid?)) d_type = myPropInfoType.Guid;
			else if (type == typeof (TimeSpan) || type == typeof (TimeSpan?)) d_type = myPropInfoType.TimeSpan;
			else if (type == typeof (StringDictionary)) d_type = myPropInfoType.StringDictionary;
			else if (type == typeof (NameValueCollection)) d_type = myPropInfoType.NameValue;
			else if (type.IsArray) {
				d.ElementType = type.GetElementType ();
				if (type == typeof (byte[]))
					d_type = myPropInfoType.ByteArray;
				else
					d_type = myPropInfoType.Array;
			}
			else if (type.Name.Contains ("Dictionary")) {
				d.GenericTypes = Reflection.Instance.GetGenericArguments (type);// t.GetGenericArguments();
				if (d.GenericTypes.Length > 0 && d.GenericTypes[0] == typeof (string))
					d_type = myPropInfoType.StringKeyDictionary;
				else
					d_type = myPropInfoType.Dictionary;
			}
#if !SILVERLIGHT
			else if (type == typeof (Hashtable)) d_type = myPropInfoType.Hashtable;
			else if (type == typeof (DataSet)) d_type = myPropInfoType.DataSet;
			else if (type == typeof (DataTable)) d_type = myPropInfoType.DataTable;
#endif
			else if (customType)
				d_type = myPropInfoType.Custom;

			if (type.IsValueType && !type.IsPrimitive && !type.IsEnum && type != typeof (decimal))
				d.IsStruct = true;

			d.IsClass = type.IsClass;
			d.IsValueType = type.IsValueType;
			if (type.IsGenericType) {
				d.IsGenericType = true;
				d.ElementType = Reflection.Instance.GetGenericArguments (type)[0];
			}

			d.PropertyType = type;
			d.Name = name;
			d.ChangeType = GetChangeType (type);
			d.Type = d_type;
			d.IsNullable = Reflection.Instance.IsNullable (type);
			return d;
		}

		private static Type GetChangeType (Type conversionType) {
			if (conversionType.IsGenericType && Reflection.Instance.GetGenericTypeDefinition (conversionType).Equals (typeof (Nullable<>)))
				return Reflection.Instance.GetGenericArguments (conversionType)[0];// conversionType.GetGenericArguments()[0];

			return conversionType;
		} 
		#endregion

		private static Dictionary<string, Enum> GetEnumValues (Type type, IReflectionController controller) {
			var ns = Enum.GetNames (type);
			var vs = Enum.GetValues (type);
			var vm = new Dictionary<string, Enum> (ns.Length);
			var sm = SerializationManager.Instance;
			for (int i = ns.Length - 1; i >= 0; i--) {
				var en = ns[i];
				var ev = (Enum)vs.GetValue (i);
				var m = type.GetMember (en)[0];
				var sn = controller.GetEnumValueName (m);
				if (String.IsNullOrEmpty (sn) == false) {
					en = sn;
				}
				sm.EnumValueCache[ev] = en;
				vm.Add (en, ev);
			}
			return vm;
		}
	}
	/// <summary>
	/// The controller interface to control type reflections for serialization and deserialization. The controller works in the reflection phase which is executed typically once and the result will be cached.
	/// </summary>
	internal interface IReflectionController
	{
		/// <summary>
		/// This method is called to override the serialized name of an enum value. If null or empty string is returned, the original name of the enum value is used.
		/// </summary>
		/// <param name="member">The enum value member.</param>
		/// <returns>The name of the enum value.</returns>
		string GetEnumValueName (MemberInfo member);

		/// <summary>
		/// This method is called before the constructor of a type is built for deserialization. If the generic parameters of a generic type, or the element type of an array type may also be checked. When this method returns true, the type can be deserialized regardless it is a non-public type. Publich types are always deserializable.
		/// </summary>
		/// <param name="type">The type to be deserialized.</param>
		/// <returns>Whether the type can be deserialized even if it is a non-public type.</returns>
		bool IsAlwaysDeserializable (Type type);

		/// <summary>
		/// This method is called to get the <see cref="IJsonInterceptor"/> for the type. If no interceptor, null should be returned.
		/// </summary>
		/// <param name="type">The type to be checked.</param>
		/// <returns>The interceptor.</returns>
		IJsonInterceptor GetInterceptor (Type type);

		/// <summary>
		/// This method is called to determine whether a field or a property is serializable.
		/// If false is returned, the member will be excluded from serialization.
		/// If true is returned, the member will always get serialized.
		/// If null is returned, the serialization of the member will be determined by the setting in <see cref="JSONParameters"/>.
		/// </summary>
		/// <param name="member">The member to be serialized.</param>
		/// <param name="isProperty">True for that the member is a property, false for a field.</param>
		/// <param name="isReadOnly">Whether the member is readonly.</param>
		/// <param name="isStatic">Whether the member is static.</param>
		/// <returns>True is returned if the member is serializable, otherwise, false.</returns>
		bool? IsMemberSerializable (MemberInfo member, bool isProperty, bool isReadOnly, bool isStatic);

		/// <summary>
		/// This method is called to determine whether a field or a property is deserializable. If false is returned, the member will be excluded from deserialization. By default, writable fields or properties are deserializable.
		/// </summary>
		/// <param name="member">The member to be serialized.</param>
		/// <returns>True is returned if the member is serializable, otherwise, false.</returns>
		bool IsMemberDeserializable (MemberInfo member);

		/// <summary>
		/// This method returns possible names for corrsponding types of a field or a property. This enables polymorphic serialization and deserialization for abstract, interface, or object types, with pre-determined concrete types. If polymorphic serialization is not used, null or an empty dictionary could be returned.
		/// </summary>
		/// <param name="member">The <see cref="MemberInfo"/> of the field or property.</param>
		/// <returns>The dictionary contains types and their corrsponding names.</returns>
		SerializedNames GetSerializedNames (MemberInfo member);

		/// <summary>
		/// This method returns a default value for a field or a property. When the value of the member has the default value, it will not be serialized. The return value of this method indicates whether the default value should be used.
		/// </summary>
		/// <param name="member">The <see cref="MemberInfo"/> of the field or property.</param>
		/// <param name="defaultValue">The default value of the member.</param>
		/// <returns>Whether the member has a default value.</returns>
		bool GetDefaultValue (MemberInfo member, out object defaultValue);

		/// <summary>
		/// This method returns the <see cref="IJsonConverter"/> to convert values for a field or a property during serialization and deserialization. If no converter is used, null can be returned.
		/// </summary>
		/// <param name="member">The <see cref="MemberInfo"/> of the field or property.</param>
		/// <returns>The converter.</returns>
		IJsonConverter GetMemberConverter (MemberInfo member);
	}

	/// <summary>
	/// Contains the names for a serialized member.
	/// </summary>
	public class SerializedNames : Dictionary<Type, string>
	{
		/// <summary>
		/// Gets the default name for the serialized member.
		/// </summary>
		public string DefaultName { get; set; }
	}

	public class DefaultReflectionController : IReflectionController
	{
		/// <summary>
		/// Ignore attributes to check for (default : XmlIgnoreAttribute).
		/// </summary>
		public List<Type> IgnoreAttributes { get; private set; }

		public DefaultReflectionController () {
			IgnoreAttributes = new List<Type> () { typeof (System.Xml.Serialization.XmlIgnoreAttribute) };
		}

		public virtual string GetEnumValueName (MemberInfo member) {
			var a = AttributeHelper.GetAttribute<JsonEnumValueAttribute> (member, false);
			if (a != null) {
				return a.Name;
			}
			return member.Name;
		}

		public virtual bool IsAlwaysDeserializable (Type type) {
			return AttributeHelper.GetAttribute<JsonSerializableAttribute> (type, false) != null;
		}

		public virtual IJsonInterceptor GetInterceptor (Type type) {
			var ia = AttributeHelper.GetAttribute<JsonInterceptorAttribute> (type, true);
			if (ia != null) {
				return ia.Interceptor;
			}
			return null;
		}

		public virtual bool? IsMemberSerializable (MemberInfo member, bool isProperty, bool isReadOnly, bool isStatic) {
			bool? s = null;
			var ic = AttributeHelper.GetAttribute<JsonIncludeAttribute> (member, true);
			if (ic != null) {
				s = ic.Include;
			}
			if (IgnoreAttributes != null && IgnoreAttributes.Count > 0) {
				foreach (var item in IgnoreAttributes) {
					if (member.IsDefined (item, false)) {
						return false;
					}
				}
			}
			return s;
		}

		public virtual bool IsMemberDeserializable (MemberInfo member) {
			var ro = AttributeHelper.GetAttribute<System.ComponentModel.ReadOnlyAttribute> (member, true);
			if (ro != null) {
				return ro.IsReadOnly == false;
			}
			return true;
		}

		public virtual SerializedNames GetSerializedNames (MemberInfo member) {
			SerializedNames tn = new SerializedNames ();
			var jf = AttributeHelper.GetAttributes<JsonFieldAttribute> (member, true);
			foreach (var item in jf) {
				if (String.IsNullOrEmpty (item.Name)) {
					continue;
				}
				if (item.Type == null) {
					tn.DefaultName = item.Name;
				}
				else {
					tn.Add (item.Type, item.Name);
				}
			}
			return tn;
		}

		public virtual bool GetDefaultValue (MemberInfo member, out object defaultValue) {
			var a = AttributeHelper.GetAttribute<System.ComponentModel.DefaultValueAttribute> (member, true);
			if (a != null) {
				defaultValue = a.Value;
				return true;
			}
			defaultValue = null;
			return false;
		}

		public virtual IJsonConverter GetMemberConverter (MemberInfo member) {
			var cv = AttributeHelper.GetAttribute<JsonConverterAttribute> (member, true);
			if (cv != null) {
				return cv.Converter;
			}
			return null;
		}
	}

}