// <file>
//     <copyright see="prj:///doc/copyright.txt"/>
//     <license see="prj:///doc/license.txt"/>
//     <owner name="Andrea Paatz" email="andrea@icsharpcode.net"/>
//     <version value="$version"/>
// </file>

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;

using ICSharpCode.Core;
using ICSharpCode.SharpDevelop.Dom;
using ICSharpCode.NRefactory.Parser.AST;
using ICSharpCode.NRefactory.Parser;

namespace ICSharpCode.SharpDevelop.Dom.NRefactoryResolver
{
	public class NRefactoryResolver : IResolver
	{
		ICompilationUnit cu;
		IClass callingClass;
		IMember callingMember;
		ICSharpCode.NRefactory.Parser.LookupTableVisitor lookupTableVisitor;
		IProjectContent projectContent = null;
		
		SupportedLanguages language;
		
		int caretLine;
		int caretColumn;
		
		public SupportedLanguages Language {
			get {
				return language;
			}
		}
		
		public IProjectContent ProjectContent {
			get {
				return projectContent;
			}
			set {
				projectContent = value;
			}
		}
		
		public ICompilationUnit CompilationUnit {
			get {
				return cu;
			}
		}
		
		public IClass CallingClass {
			get {
				return callingClass;
			}
		}
		
		public int CaretLine {
			get {
				return caretLine;
			}
		}
		
		public int CaretColumn {
			get {
				return caretColumn;
			}
		}
		
		LanguageProperties languageProperties;
		
		public LanguageProperties LanguageProperties {
			get {
				return languageProperties;
			}
		}
		
		public NRefactoryResolver(SupportedLanguages language)
		{
			this.language = language;
			this.projectContent = ParserService.CurrentProjectContent;
			switch (language) {
				case SupportedLanguages.CSharp:
					languageProperties = LanguageProperties.CSharp;
					break;
				case SupportedLanguages.VBNet:
					languageProperties = LanguageProperties.VBNet;
					break;
				default:
					throw new NotSupportedException("The language " + language + " is not supported in the resolver");
			}
		}
		
		public ResolveResult Resolve(string expression,
		                             int caretLineNumber,
		                             int caretColumn,
		                             string fileName)
		{
			if (expression == null) {
				expression = "";
			}
			expression = expression.TrimStart(null);
			
			this.caretLine   = caretLineNumber;
			this.caretColumn = caretColumn;
			
			ParseInformation parseInfo = ParserService.GetParseInformation(fileName);
			if (parseInfo == null) {
				return null;
			}
			ICSharpCode.NRefactory.Parser.AST.CompilationUnit fileCompilationUnit = parseInfo.MostRecentCompilationUnit.Tag as ICSharpCode.NRefactory.Parser.AST.CompilationUnit;
			if (fileCompilationUnit == null) {
//				ICSharpCode.NRefactory.Parser.Parser fileParser = new ICSharpCode.NRefactory.Parser.Parser();
//				fileParser.Parse(new Lexer(new StringReader(fileContent)));
				return null;
			}
			Expression expr = null;
			if (expression == "") {
				if ((expr = WithResolve()) == null) {
					return null;
				}
			}
			if (expr == null) {
				expr = SpecialConstructs(expression);
				if (expr == null) {
					ICSharpCode.NRefactory.Parser.IParser p = ICSharpCode.NRefactory.Parser.ParserFactory.CreateParser(language, new System.IO.StringReader(expression));
					expr = p.ParseExpression();
					if (expr == null) {
						return null;
					}
				}
			}
			lookupTableVisitor = new LookupTableVisitor(languageProperties.NameComparer);
			lookupTableVisitor.Visit(fileCompilationUnit, null);
			
			NRefactoryASTConvertVisitor cSharpVisitor = new NRefactoryASTConvertVisitor(parseInfo.MostRecentCompilationUnit != null ? parseInfo.MostRecentCompilationUnit.ProjectContent : null);
			
			cu = (ICompilationUnit)cSharpVisitor.Visit(fileCompilationUnit, null);
			
			if (cu != null) {
				callingClass = cu.GetInnermostClass(caretLine, caretColumn);
				cu.FileName = fileName;
			}
			callingMember = GetCurrentMember();
			
			TypeVisitor typeVisitor = new TypeVisitor(this);
			
			if (expr is PrimitiveExpression) {
				if (((PrimitiveExpression)expr).Value is int)
					return null;
			} else if (expr is InvocationExpression) {
				IMethod method = typeVisitor.GetMethod((InvocationExpression)expr, null);
				if (method != null)
					return CreateMemberResolveResult(method);
			} else if (expr is FieldReferenceExpression) {
				FieldReferenceExpression fieldReferenceExpression = (FieldReferenceExpression)expr;
				IReturnType returnType;
				if (fieldReferenceExpression.FieldName == null || fieldReferenceExpression.FieldName == "") {
					if (fieldReferenceExpression.TargetObject is TypeReferenceExpression) {
						returnType = TypeVisitor.CreateReturnType(((TypeReferenceExpression)fieldReferenceExpression.TargetObject).TypeReference, this);
						if (returnType != null) {
							IClass c = projectContent.GetClass(returnType.FullyQualifiedName);
							if (c != null)
								return new TypeResolveResult(callingClass, callingMember, returnType, c);
						}
					}
				}
				returnType = fieldReferenceExpression.TargetObject.AcceptVisitor(typeVisitor, null) as IReturnType;
				if (returnType != null) {
					string name = SearchNamespace(returnType.FullyQualifiedName, this.CompilationUnit);
					if (name != null) {
						name += "." + fieldReferenceExpression.FieldName;
						string n = SearchNamespace(name, null);
						if (n != null) {
							return new NamespaceResolveResult(callingClass, callingMember, n);
						}
						IClass c = SearchType(name, this.CallingClass, this.CompilationUnit);
						if (c != null) {
							return new TypeResolveResult(callingClass, callingMember, c.DefaultReturnType, c);
						}
						return null;
					}
					IMember member = GetMember(returnType, fieldReferenceExpression.FieldName);
					if (member != null)
						return CreateMemberResolveResult(member);
					ResolveResult result = ResolveMethod(returnType, fieldReferenceExpression.FieldName);
					if (result != null)
						return result;
				}
			} else if (expr is IdentifierExpression) {
				ResolveResult result = ResolveIdentifier(((IdentifierExpression)expr).Identifier);
				if (result != null)
					return result;
			}
			IReturnType type = expr.AcceptVisitor(typeVisitor, null) as IReturnType;
			
			if (type == null || type.FullyQualifiedName == "") {
				return null;
			}
			if (expr is ObjectCreateExpression) {
				foreach (IMethod m in type.GetMethods()) {
					if (m.IsConstructor && !m.IsStatic)
						return CreateMemberResolveResult(m);
				}
				return null;
			}
			return new ResolveResult(callingClass, callingMember, FixType(type));
		}
		
		IReturnType FixType(IReturnType type)
		{
			return type;
		}
		
		#region Resolve Identifier
		ResolveResult ResolveIdentifier(string identifier)
		{
			string name = SearchNamespace(identifier, this.CompilationUnit);
			if (name != null && name != "") {
				return new NamespaceResolveResult(callingClass, callingMember, name);
			}
			if (callingMember != null) { // LocalResolveResult requires callingMember to be set
				LocalLookupVariable var = SearchVariable(identifier);
				if (var != null) {
					IReturnType type = GetVariableType(var);
					IField field = new DefaultField(FixType(type), identifier, ModifierEnum.None, new DefaultRegion(var.StartPos, var.EndPos), callingClass);
					return new LocalResolveResult(callingMember, field, false);
				}
				IParameter para = SearchMethodParameter(identifier);
				if (para != null) {
					IField field = new DefaultField(FixType(para.ReturnType), para.Name, ModifierEnum.None, para.Region, callingClass);
					return new LocalResolveResult(callingMember, field, true);
				}
			}
			if (callingClass != null) {
				IMember member = GetMember(callingClass.DefaultReturnType, identifier);
				if (member != null) {
					return CreateMemberResolveResult(member);
				}
				ResolveResult result = ResolveMethod(callingClass.DefaultReturnType, identifier);
				if (result != null)
					return result;
				
				IClass c = SearchType(identifier, callingClass, cu);
				if (c != null) {
					return new TypeResolveResult(callingClass, callingMember, c.DefaultReturnType, c);
				}
			}
			
			// try if there exists a static member in outer classes named typeName
			List<IClass> classes = cu.GetOuterClasses(caretLine, caretColumn);
			foreach (IClass c2 in classes) {
				IMember member = GetMember(c2.DefaultReturnType, identifier);
				if (member != null && member.IsStatic) {
					return new MemberResolveResult(callingClass, callingMember, member);
				}
			}
			return null;
		}
		#endregion
		
		private ResolveResult CreateMemberResolveResult(IMember member)
		{
			member.ReturnType = FixType(member.ReturnType);
			return new MemberResolveResult(callingClass, callingMember, member);
		}
		
		#region ResolveMethod
		ResolveResult ResolveMethod(IReturnType type, string identifier)
		{
			if (type == null)
				return null;
			foreach (IMethod method in type.GetMethods()) {
				if (IsSameName(identifier, method.Name))
					return new MethodResolveResult(callingClass, callingMember, type, identifier);
			}
			return null;
		}
		#endregion
		
		Expression WithResolve()
		{
			if (language != SupportedLanguages.VBNet) {
				return null;
			}
			Expression expr = null;
			// TODO :
//			if (lookupTableVisitor.WithStatements != null) {
//				foreach (WithStatement with in lookupTableVisitor.WithStatements) {
//					if (IsInside(new Point(caretColumn, caretLine), with.StartLocation, with.EndLocation)) {
//						expr = with.WithExpression;
//					}
//				}
//			}
			return expr;
		}
		
		Expression SpecialConstructs(string expression)
		{
			if (language == SupportedLanguages.VBNet) {
				// MyBase and MyClass are no expressions, only MyBase.Identifier and MyClass.Identifier
				if (expression == "mybase") {
					return new BaseReferenceExpression();
				} else if (expression == "myclass") {
					return new ClassReferenceExpression();
				}
			}
			return null;
		}
		
		bool IsSameName(string name1, string name2)
		{
			return languageProperties.NameComparer.Equals(name1, name2);
		}
		
		bool IsInside(Point between, Point start, Point end)
		{
			if (between.Y < start.Y || between.Y > end.Y) {
				return false;
			}
			if (between.Y > start.Y) {
				if (between.Y < end.Y) {
					return true;
				}
				// between.Y == end.Y
				return between.X <= end.X;
			}
			// between.Y == start.Y
			if (between.X < start.X) {
				return false;
			}
			// start is OK and between.Y <= end.Y
			return between.Y < end.Y || between.X <= end.X;
		}
		
		IMember GetCurrentMember()
		{
			if (callingClass == null)
				return null;
			foreach (IProperty property in callingClass.Properties) {
				if (property.BodyRegion != null && property.BodyRegion.IsInside(caretLine, caretColumn)) {
					return property;
				}
			}
			foreach (IMethod method in callingClass.Methods) {
				if (method.BodyRegion != null && method.BodyRegion.IsInside(caretLine, caretColumn)) {
					return method;
				}
			}
			return null;
		}
		
		/// <remarks>
		/// use the usings to find the correct name of a namespace
		/// </remarks>
		public string SearchNamespace(string name, ICompilationUnit unit)
		{
			return projectContent.SearchNamespace(name, unit, caretLine, caretColumn);
		}
		
		/// <remarks>
		/// use the usings and the name of the namespace to find a class
		/// </remarks>
		public IClass SearchType(string name, IClass curType)
		{
			return projectContent.SearchType(name, curType, caretLine, caretColumn);
		}
		
		/// <remarks>
		/// use the usings and the name of the namespace to find a class
		/// </remarks>
		public IClass SearchType(string name, IClass curType, ICompilationUnit unit)
		{
			return projectContent.SearchType(name, curType, unit, caretLine, caretColumn);
		}
		
		#region Helper for TypeVisitor
		#region SearchMethod
		/// <summary>
		/// Gets the list of methods on the return type that have the specified name.
		/// </summary>
		public ArrayList SearchMethod(IReturnType type, string memberName)
		{
			ArrayList methods = new ArrayList();
			if (type == null)
				return methods;
			
			//bool isClassInInheritanceTree = false;
			//if (callingClass != null)
			//	isClassInInheritanceTree = callingClass.IsTypeInInheritanceTree(curType);
			
			foreach (IMethod m in type.GetMethods()) {
				if (IsSameName(m.Name, memberName)
				    // && m.IsAccessible(callingClass, isClassInInheritanceTree)
				   ) {
					methods.Add(m);
				}
			}
			return methods;
		}
		#endregion
		
		#region SearchMember
		// no methods or indexer
		public IReturnType SearchMember(IReturnType type, string memberName)
		{
			if (type == null)
				return null;
			//bool isClassInInheritanceTree = false;
			//if (callingClass != null)
			//	isClassInInheritanceTree = callingClass.IsTypeInInheritanceTree(curType);
			//foreach (IClass c in curType.InnerClasses) {
			//	if (IsSameName(c.Name, memberName) && c.IsAccessible(callingClass, isClassInInheritanceTree)) {
			//		return new ReturnType(c.FullyQualifiedName);
			//	}
			//}
			IMember member = GetMember(type, memberName);
			if (member == null)
				return null;
			else
				return member.ReturnType;
		}
		
		public IMember GetMember(IReturnType type, string memberName)
		{
			if (type == null)
				return null;
			//bool isClassInInheritanceTree = false;
			//if (callingClass != null)
			//	isClassInInheritanceTree = callingClass.IsTypeInInheritanceTree(c);
			foreach (IProperty p in type.GetProperties()) {
				if (IsSameName(p.Name, memberName)) {
					return p;
				}
			}
			foreach (IField f in type.GetFields()) {
				if (IsSameName(f.Name, memberName)) {
					return f;
				}
			}
			foreach (IEvent e in type.GetEvents()) {
				if (IsSameName(e.Name, memberName)) {
					return e;
				}
			}
			return null;
		}
		#endregion
		
		#region DynamicLookup
		/// <remarks>
		/// does the dynamic lookup for the identifier
		/// </remarks>
		public IReturnType DynamicLookup(string typeName)
		{
			// try if it exists a variable named typeName
			IReturnType variable = GetVariableType(SearchVariable(typeName));
			if (variable != null) {
				return variable;
			}
			
			if (callingClass == null) {
				return null;
			}
			
			// try if typeName is a method parameter
			IParameter parameter = SearchMethodParameter(typeName);
			if (parameter != null) {
				return parameter.ReturnType;
			}
			
			// check if typeName == value in set method of a property
			if (typeName == "value") {
				IProperty property = callingMember as IProperty;
				if (property != null && property.SetterRegion != null && property.SetterRegion.IsInside(caretLine, caretColumn)) {
					return property.ReturnType;
				}
			}
			
			// try if there exists a nonstatic member named typeName
			IReturnType t = SearchMember(callingClass.DefaultReturnType, typeName);
			if (t != null) {
				return t;
			}
			
			// try if there exists a static member in outer classes named typeName
			List<IClass> classes = cu.GetOuterClasses(caretLine, caretColumn);
			foreach (IClass c in classes) {
				IMember member = GetMember(c.DefaultReturnType, typeName);
				if (member != null && member.IsStatic) {
					return member.ReturnType;
				}
			}
			return null;
		}
		
		IParameter SearchMethodParameter(string parameter)
		{
			IMethod method = callingMember as IMethod;
			if (method == null) {
				return null;
			}
			foreach (IParameter p in method.Parameters) {
				if (IsSameName(p.Name, parameter)) {
					return p;
				}
			}
			return null;
		}
		
		IReturnType GetVariableType(LocalLookupVariable v)
		{
			if (v == null) {
				return null;
			}
			return TypeVisitor.CreateReturnType(v.TypeRef, this);
		}
		
		LocalLookupVariable SearchVariable(string name)
		{
			if (!lookupTableVisitor.Variables.ContainsKey(name))
				return null;
			List<LocalLookupVariable> variables = lookupTableVisitor.Variables[name];
			if (variables.Count <= 0) {
				return null;
			}
			
			foreach (LocalLookupVariable v in variables) {
				if (IsInside(new Point(caretColumn, caretLine), v.StartPos, v.EndPos)) {
					return v;
				}
			}
			return null;
		}
		#endregion
		#endregion
		
		public ArrayList CtrlSpace(int caretLine, int caretColumn, string fileName)
		{
			ArrayList result;
			if (language == SupportedLanguages.VBNet) {
				result = new ArrayList();
				foreach (string primitive in TypeReference.GetPrimitiveTypesVB()) {
					result.Add(Char.ToUpper(primitive[0]) + primitive.Substring(1));
				}
			} else {
				result = new ArrayList(TypeReference.GetPrimitiveTypes());
			}
			ParseInformation parseInfo = ParserService.GetParseInformation(fileName);
			ICSharpCode.NRefactory.Parser.AST.CompilationUnit fileCompilationUnit = parseInfo.MostRecentCompilationUnit.Tag as ICSharpCode.NRefactory.Parser.AST.CompilationUnit;
			if (fileCompilationUnit == null) {
				return null;
			}
			lookupTableVisitor = new LookupTableVisitor(languageProperties.NameComparer);
			lookupTableVisitor.Visit(fileCompilationUnit, null);
			
			NRefactoryASTConvertVisitor cSharpVisitor = new NRefactoryASTConvertVisitor(parseInfo.MostRecentCompilationUnit != null ? parseInfo.MostRecentCompilationUnit.ProjectContent : null);
			cu = (ICompilationUnit)cSharpVisitor.Visit(fileCompilationUnit, null);
			if (cu != null) {
				callingClass = cu.GetInnermostClass(caretLine, caretColumn);
				if (callingClass != null) {
					IMethod method = callingMember as IMethod;
					if (method != null) {
						foreach (IParameter p in method.Parameters) {
							result.Add(new DefaultField(p.ReturnType, p.Name, ModifierEnum.None, method.Region, callingClass));
						}
					}
					result.AddRange(projectContent.GetNamespaceContents(callingClass.Namespace));
					bool inStatic = true;
					if (callingMember != null)
						inStatic = callingMember.IsStatic;
					//result.AddRange(callingClass.GetAccessibleMembers(callingClass, inStatic).ToArray());
					//if (inStatic == false) {
					//	result.AddRange(callingClass.GetAccessibleMembers(callingClass, !inStatic).ToArray());
					//}
				}
			}
			foreach (KeyValuePair<string, List<LocalLookupVariable>> pair in lookupTableVisitor.Variables) {
				if (pair.Value != null && pair.Value.Count > 0) {
					foreach (LocalLookupVariable v in pair.Value) {
						if (IsInside(new Point(caretColumn, caretLine), v.StartPos, v.EndPos)) {
							// convert to a field for display
							result.Add(new DefaultField(TypeVisitor.CreateReturnType(v.TypeRef, this), pair.Key, ModifierEnum.None, new DefaultRegion(v.StartPos, v.EndPos), callingClass));
							break;
						}
					}
				}
			}
			projectContent.AddNamespaceContents(result, "", languageProperties, true);
			foreach (IUsing u in cu.Usings) {
				if (u != null) {
					foreach (string name in u.Usings) {
						foreach (object o in projectContent.GetNamespaceContents(name)) {
							if (!(o is string))
								result.Add(o);
						}
					}
					foreach (string alias in u.Aliases.Keys) {
						result.Add(alias);
					}
				}
			}
			return result;
		}
	}
}
