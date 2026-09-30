namespace Gatto.Terminal;

//the kind of thing a char of code is. a lexer answers with roles, and only the theme turns a role into a colour
public enum SpanRole { None, Keyword, Type, String, Number, Comment, Punct, Variable, Parameter, Function }

public enum CodeLanguage { None, CSharp, PowerShell, Json, JavaScript, TypeScript, Python, Xml, Html, Rust, Bash }
