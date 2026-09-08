#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace IntegratedModManager.Config;

public sealed class ImmPatchExpressionContext
{
	public JToken? Setting { get; init; }
	public JToken Current { get; init; } = JValue.CreateNull();

	public IReadOnlyDictionary<string, JToken> Settings { get; init; } = new Dictionary<string, JToken>();
	public IReadOnlyDictionary<string, JToken> Constants { get; init; } = new Dictionary<string, JToken>();
}

public sealed class ImmPatchExpression
{
	private const double BooleanEpsilon = 1e-10;

	private readonly ExpressionNode Root;

	public IReadOnlyCollection<string> SettingReferences => SettingReferenceSet;
	public IReadOnlyCollection<string> ConstantReferences => ConstantReferenceSet;
	public IReadOnlyCollection<string> BareReferences => BareReferenceSet;

	public bool UsesOwningSetting { get; }
	public bool UsesCurrentValue { get; }

	private readonly HashSet<string> SettingReferenceSet;
	private readonly HashSet<string> ConstantReferenceSet;
	private readonly HashSet<string> BareReferenceSet;

	private ImmPatchExpression(ExpressionNode root, HashSet<string> settingReferences, HashSet<string> constantReferences, HashSet<string> bareReferences)
	{
		Root = root;
		SettingReferenceSet = settingReferences;
		ConstantReferenceSet = constantReferences;
		BareReferenceSet = bareReferences;
		UsesOwningSetting = root.UsesOwningSetting;
		UsesCurrentValue = root.UsesCurrentValue;
	}

	public static ImmPatchExpression Compile(string expression)
	{
		if (string.IsNullOrWhiteSpace(expression))
		{
			throw new FormatException("Patch expression cannot be empty.");
		}

		Parser parser = new(expression);
		ExpressionNode root = parser.Parse();

		return new ImmPatchExpression(root, parser.SettingReferences, parser.ConstantReferences, parser.BareReferences);
	}

	public JToken Evaluate(ImmPatchExpressionContext context) { return Root.Evaluate(context); }
	public bool EvaluateBoolean(ImmPatchExpressionContext context) { return AsBoolean(Root.Evaluate(context)); }

	private abstract class ExpressionNode
	{
		public virtual bool UsesOwningSetting => false;
		public virtual bool UsesCurrentValue => false;

		public abstract JToken Evaluate(ImmPatchExpressionContext context);
	}

	private sealed class LiteralNode : ExpressionNode
	{
		private readonly JToken Value;
		public LiteralNode(JToken value) { Value = value; }

		public override JToken Evaluate(ImmPatchExpressionContext context) { return Value.DeepClone(); }
	}

	private enum ReferenceKind
	{
		Setting,
		Current,
		NamedSetting,
		NamedConstant,
		Bare
	}

	private sealed class ReferenceNode : ExpressionNode
	{
		private readonly ReferenceKind Kind;
		private readonly string Name;

		public ReferenceNode(ReferenceKind kind, string name = "")
		{
			Kind = kind;
			Name = name;
		}

		public override bool UsesOwningSetting => Kind == ReferenceKind.Setting;
		public override bool UsesCurrentValue => Kind == ReferenceKind.Current;

		public override JToken Evaluate(ImmPatchExpressionContext context)
		{
			switch (Kind)
			{
				case ReferenceKind.Setting:
					if (context.Setting == null)
					{
						throw new InvalidOperationException("Expression uses Setting outside a setting-owned target.");
					}

				return context.Setting.DeepClone();

				case ReferenceKind.Current: return context.Current.DeepClone();

				case ReferenceKind.NamedSetting:
					if (!context.Settings.TryGetValue(Name, out JToken? setting))
					{
						throw new KeyNotFoundException($"Patch setting '{Name}' is not available.");
					}

				return setting.DeepClone();

				case ReferenceKind.NamedConstant:
					if (!context.Constants.TryGetValue(Name, out JToken? constant))
					{
						throw new KeyNotFoundException($"Patch constant '{Name}' is not available.");
					}

				return constant.DeepClone();

				case ReferenceKind.Bare:
					if (context.Settings.TryGetValue(Name, out JToken? bareSetting)) { return bareSetting.DeepClone(); }

					if (context.Constants.TryGetValue(Name, out JToken? bareConstant)) { return bareConstant.DeepClone(); }

				throw new KeyNotFoundException($"Patch expression identifier '{Name}' is not available.");

				default: throw new InvalidOperationException("Unsupported expression reference.");
			}
		}
	}

	private sealed class UnaryNode : ExpressionNode
	{
		private readonly TokenKind Operator;
		private readonly ExpressionNode Operand;

		public UnaryNode(TokenKind @operator, ExpressionNode operand)
		{
			Operator = @operator;
			Operand = operand;
		}

		public override bool UsesOwningSetting => Operand.UsesOwningSetting;
		public override bool UsesCurrentValue => Operand.UsesCurrentValue;

		public override JToken Evaluate(ImmPatchExpressionContext context) { JToken value = Operand.Evaluate(context); return Operator switch { TokenKind.Bang => new JValue(!AsBoolean(value)), TokenKind.Plus => NumberResult(RequireNumber(value)), TokenKind.Minus => NumberResult(-RequireNumber(value)), _ => throw new InvalidOperationException("Unsupported unary expression operator.") }; }
	}

	private sealed class BinaryNode : ExpressionNode
	{
		private readonly TokenKind Operator;
		private readonly ExpressionNode Left;
		private readonly ExpressionNode Right;

		public BinaryNode(ExpressionNode left, TokenKind @operator, ExpressionNode right)
		{
			Left = left;
			Operator = @operator;
			Right = right;
		}

		public override bool UsesOwningSetting => Left.UsesOwningSetting || Right.UsesOwningSetting;
		public override bool UsesCurrentValue => Left.UsesCurrentValue || Right.UsesCurrentValue;

		public override JToken Evaluate(ImmPatchExpressionContext context)
		{
			if (Operator == TokenKind.AndAnd)
			{
				JToken left = Left.Evaluate(context);
				if (!AsBoolean(left)) { return new JValue(false); }

				return new JValue(AsBoolean(Right.Evaluate(context)));
			}

			if (Operator == TokenKind.OrOr)
			{
				JToken left = Left.Evaluate(context);
				if (AsBoolean(left)) { return new JValue(true); }

				return new JValue(AsBoolean(Right.Evaluate(context)));
			}

			JToken leftValue = Left.Evaluate(context);
			JToken rightValue = Right.Evaluate(context);

			switch (Operator)
			{
				case TokenKind.Plus: return NumberResult(RequireNumber(leftValue) + RequireNumber(rightValue));
				case TokenKind.Minus: return NumberResult(RequireNumber(leftValue) - RequireNumber(rightValue));
				case TokenKind.Star: return NumberResult(RequireNumber(leftValue) * RequireNumber(rightValue));
				case TokenKind.Slash: return NumberResult(RequireNumber(leftValue) / RequireNumber(rightValue));
				case TokenKind.Percent: return NumberResult(RequireNumber(leftValue) % RequireNumber(rightValue));
				case TokenKind.EqualEqual: return new JValue(ValuesEqual(leftValue, rightValue));
				case TokenKind.BangEqual: return new JValue(!ValuesEqual(leftValue, rightValue));
				case TokenKind.Less: return new JValue(RequireNumber(leftValue) < RequireNumber(rightValue));
				case TokenKind.LessEqual: return new JValue(RequireNumber(leftValue) <= RequireNumber(rightValue));
				case TokenKind.Greater: return new JValue(RequireNumber(leftValue) > RequireNumber(rightValue));
				case TokenKind.GreaterEqual: return new JValue(RequireNumber(leftValue) >= RequireNumber(rightValue));

				default:
					throw new InvalidOperationException("Unsupported binary expression operator.");
			}
		}
	}

	private sealed class ConditionalNode : ExpressionNode
	{
		private readonly ExpressionNode Condition;
		private readonly ExpressionNode WhenTrue;
		private readonly ExpressionNode WhenFalse;

		public ConditionalNode(ExpressionNode condition, ExpressionNode whenTrue, ExpressionNode whenFalse)
		{
			Condition = condition;
			WhenTrue = whenTrue;
			WhenFalse = whenFalse;
		}

		public override bool UsesOwningSetting => Condition.UsesOwningSetting || WhenTrue.UsesOwningSetting || WhenFalse.UsesOwningSetting;
		public override bool UsesCurrentValue => Condition.UsesCurrentValue || WhenTrue.UsesCurrentValue || WhenFalse.UsesCurrentValue;

		public override JToken Evaluate(ImmPatchExpressionContext context) { return AsBoolean(Condition.Evaluate(context)) ? WhenTrue.Evaluate(context) : WhenFalse.Evaluate(context); }
	}

	private sealed class FunctionNode : ExpressionNode
	{
		private readonly string Name;
		private readonly ExpressionNode[] Arguments;

		public FunctionNode(string name, ExpressionNode[] arguments)
		{
			Name = name;
			Arguments = arguments;
		}

		public override bool UsesOwningSetting => Arguments.Any(argument => argument.UsesOwningSetting);
		public override bool UsesCurrentValue => Arguments.Any(argument => argument.UsesCurrentValue);

		public override JToken Evaluate(ImmPatchExpressionContext context)
		{
			string name = Name.ToLowerInvariant();

			switch (name)
			{
				case "if": RequireArity(3);

				return AsBoolean(Arguments[0].Evaluate(context)) ? Arguments[1].Evaluate(context) : Arguments[2].Evaluate(context);

				case "not":
					RequireArity(1);
				return new JValue(!AsBoolean(Arguments[0].Evaluate(context)));

				case "and":
					if (Arguments.Length < 2)
					{
						throw new InvalidOperationException("and() requires at least two arguments.");
					}

					foreach (ExpressionNode argument in Arguments)
					{
						if (!AsBoolean(argument.Evaluate(context))) { return new JValue(false); }
					}
				return new JValue(true);

				case "or":
					if (Arguments.Length < 2)
					{
						throw new InvalidOperationException("or() requires at least two arguments.");
					}

					foreach (ExpressionNode argument in Arguments)
					{
						if (AsBoolean(argument.Evaluate(context))) { return new JValue(true); }
					}
				return new JValue(false);

				case "greater":
					RequireArity(4);
				return RequireNumber(Arguments[0].Evaluate(context)) > RequireNumber(Arguments[1].Evaluate(context)) ? Arguments[2].Evaluate(context) : Arguments[3].Evaluate(context);

				case "lesser":
					RequireArity(4);
				return RequireNumber(Arguments[0].Evaluate(context)) < RequireNumber(Arguments[1].Evaluate(context)) ? Arguments[2].Evaluate(context) : Arguments[3].Evaluate(context);

				case "equal":
					RequireArity(4);
				return ValuesEqual(Arguments[0].Evaluate(context), Arguments[1].Evaluate(context)) ? Arguments[2].Evaluate(context) : Arguments[3].Evaluate(context);

				case "notequal":
					RequireArity(4);
				return !ValuesEqual(Arguments[0].Evaluate(context), Arguments[1].Evaluate(context)) ? Arguments[2].Evaluate(context) : Arguments[3].Evaluate(context);
			}

			JToken[] values = Arguments.Select(argument => argument.Evaluate(context)).ToArray();

			double Number(int index) => RequireNumber(values[index]);

			switch (name)
			{
				case "sin":
					RequireArity(values, 1);
				return NumberResult(Math.Sin(Number(0)));

				case "cos":
					RequireArity(values, 1);
				return NumberResult(Math.Cos(Number(0)));

				case "abs":
					RequireArity(values, 1);
				return NumberResult(Math.Abs(Number(0)));

				case "sqrt":
					RequireArity(values, 1);
				return NumberResult(Math.Sqrt(Number(0)));

				case "ceiling":
					RequireArity(values, 1);
				return NumberResult(Math.Ceiling(Number(0)));

				case "floor":
					RequireArity(values, 1);
				return NumberResult(Math.Floor(Number(0)));

				case "exp":
					RequireArity(values, 1);
				return NumberResult(Math.Exp(Number(0)));

				case "log":
					if (values.Length == 1) { return NumberResult(Math.Log(Number(0))); }

					if (values.Length == 2) { return NumberResult(Math.Log(Number(0), Number(1))); }

				throw new InvalidOperationException("log() requires one or two arguments.");

				case "round":
					if (values.Length == 1) { return NumberResult(Math.Round(Number(0))); }

					if (values.Length == 2)
					{
						double digitsValue = Number(1);
						int digits = checked((int)digitsValue);

						if (Math.Abs(digitsValue - digits) > double.Epsilon || digits < 0 || digits > 15)
						{
							throw new InvalidOperationException("round() digits must be an integer from 0 through 15.");
						}

						return NumberResult(Math.Round(Number(0), digits));
					}

				throw new InvalidOperationException("round() requires one or two arguments.");

				case "sign":
					RequireArity(values, 1);
				return NumberResult(Math.Sign(Number(0)));

				case "clamp":
					RequireArity(values, 3);
				return NumberResult(Math.Clamp(Number(0), Number(1), Number(2)));

				case "max":
					if (values.Length == 0)
					{
						throw new InvalidOperationException("max() requires at least one argument.");
					}

				return NumberResult(values.Max(RequireNumber));

				case "min":
					if (values.Length == 0)
					{
						throw new InvalidOperationException("min() requires at least one argument.");
					}

				return NumberResult(values.Min(RequireNumber));

				default: throw new InvalidOperationException($"Unknown patch expression function '{Name}'.");
			}
		}

		private void RequireArity(int count)
		{
			if (Arguments.Length != count)
			{
				throw new InvalidOperationException($"{Name}() requires {count} arguments.");
			}
		}

		private void RequireArity(JToken[] values, int count)
		{
			if (values.Length != count)
			{
				throw new InvalidOperationException($"{Name}() requires {count} arguments.");
			}
		}
	}

	private static JToken NumberResult(double value)
	{
		if (!double.IsFinite(value))
		{
			throw new InvalidOperationException("Patch expression produced a non-finite number.");
		}

		return new JValue(value);
	}

	private static bool ValuesEqual(JToken left, JToken right)
	{
		if (TryNumber(left, out double leftNumber) && TryNumber(right, out double rightNumber)) { return leftNumber.Equals(rightNumber); }

		return JToken.DeepEquals(left, right);
	}

	private static bool AsBoolean(JToken token)
	{
		if (token.Type == JTokenType.Boolean) { return token.Value<bool>(); }
		if (TryNumber(token, out double number)) { return Math.Abs(number) > BooleanEpsilon; }

		if (token.Type is JTokenType.Null or JTokenType.Undefined) { return false; }
		if (token.Type == JTokenType.String) { return !string.IsNullOrEmpty(token.Value<string>()); }

		throw new InvalidOperationException($"Cannot convert {token.Type} to Boolean.");
	}

	private static bool TryNumber(JToken token, out double value)
	{
		value = 0;

		if (token.Type == JTokenType.Boolean) { value = token.Value<bool>() ? 1 : 0; return true; }
		if (token.Type is not (JTokenType.Integer or JTokenType.Float)) { return false; }

		try { value = token.Value<double>(); return double.IsFinite(value); }
		catch { return false; }
	}

	private static double RequireNumber(JToken token)
	{
		if (!TryNumber(token, out double value))
		{
			throw new InvalidOperationException($"Expected a numeric expression value, received {token.Type}.");
		}

		return value;
	}

	private enum TokenKind
	{
		End,
		Identifier,
		Number,
		String,

		LeftParen,
		RightParen,
		LeftBracket,
		RightBracket,
		Comma,
		Dot,
		Question,
		Colon,

		Plus,
		Minus,
		Star,
		Slash,
		Percent,
		Bang,

		AndAnd,
		OrOr,
		EqualEqual,
		BangEqual,
		Less,
		LessEqual,
		Greater,
		GreaterEqual
	}

	private readonly record struct Token(TokenKind Kind, string Text, double Number = 0);

	private sealed class Lexer
	{
		private readonly string Source;
		private int Position;

		public Lexer(string source) { Source = source; }

		public Token Next()
		{
			SkipWhitespace();

			if (Position >= Source.Length) { return new Token(TokenKind.End, ""); }

			char current = Source[Position];

			if (char.IsLetter(current) || current == '_') { return ReadIdentifier(); }
			if (char.IsDigit(current) || (current == '.' && Position + 1 < Source.Length && char.IsDigit(Source[Position + 1]))) { return ReadNumber(); }

			if (current is '"' or '\'') { return ReadString(); }

			Position++;

			switch (current)
			{
				case '(': return new Token(TokenKind.LeftParen, "(");
				case ')': return new Token(TokenKind.RightParen, ")");
				case '[': return new Token(TokenKind.LeftBracket, "[");
				case ']': return new Token(TokenKind.RightBracket, "]");
				case ',': return new Token(TokenKind.Comma, ",");
				case '.': return new Token(TokenKind.Dot, ".");
				case '?': return new Token(TokenKind.Question, "?");
				case ':': return new Token(TokenKind.Colon, ":");
				case '+': return new Token(TokenKind.Plus, "+");
				case '-': return new Token(TokenKind.Minus, "-");
				case '*': return new Token(TokenKind.Star, "*");
				case '/': return new Token(TokenKind.Slash, "/");
				case '%': return new Token(TokenKind.Percent, "%");

				case '!':
					if (Match('=')) { return new Token(TokenKind.BangEqual, "!="); }

				return new Token(TokenKind.Bang, "!");

				case '&':
					if (Match('&')) { return new Token(TokenKind.AndAnd, "&&"); }

				break;

				case '|':
					if (Match('|')) { return new Token(TokenKind.OrOr, "||"); }

				break;

				case '=':
					if (Match('=')) { return new Token(TokenKind.EqualEqual, "=="); }

				break;

				case '<':
					if (Match('=')) { return new Token(TokenKind.LessEqual, "<="); }

				return new Token(TokenKind.Less, "<");

				case '>':
					if (Match('=')) { return new Token(TokenKind.GreaterEqual, ">="); }

				return new Token(TokenKind.Greater, ">");
			}

			throw new FormatException($"Unexpected character '{current}' at expression position {Position}.");
		}

		private bool Match(char expected)
		{
			if (Position >= Source.Length || Source[Position] != expected) { return false; }

			Position++;
			return true;
		}

		private Token ReadIdentifier()
		{
			int start = Position++;

			while (Position < Source.Length && (char.IsLetterOrDigit(Source[Position]) || Source[Position] == '_')) { Position++; }
			return new Token(TokenKind.Identifier, Source[start..Position]);
		}

		private Token ReadNumber()
		{
			int start = Position;
			bool hasExponent = false;

			while (Position < Source.Length)
			{
				char value = Source[Position];

				if (char.IsDigit(value) || value == '.') { Position++; continue; }

				if ((value is 'e' or 'E') && !hasExponent)
				{
					hasExponent = true;
					Position++;

					if (Position < Source.Length && Source[Position] is '+' or '-') { Position++; }

					continue;
				}

				break;
			}

			string text = Source[start..Position];

			if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
			{
				throw new FormatException($"Invalid number '{text}' in patch expression.");
			}

			return new Token(TokenKind.Number, text, number);
		}

		private Token ReadString()
		{
			char quote = Source[Position++];
			System.Text.StringBuilder result = new();

			while (Position < Source.Length)
			{
				char value = Source[Position++];

				if (value == quote) { return new Token(TokenKind.String, result.ToString()); }
				if (value != '\\') { result.Append(value); continue; }

				if (Position >= Source.Length)
				{
					throw new FormatException("Unterminated escape sequence in patch expression string.");
				}

				char escaped = Source[Position++];

				switch (escaped)
				{
					case '\\':
						result.Append('\\');
					break;

					case '"':
						result.Append('"');
					break;

					case '\'':
						result.Append('\'');
					break;

					case 'n':
						result.Append('\n');
					break;

					case 'r':
						result.Append('\r');
					break;

					case 't':
						result.Append('\t');
					break;

					case 'b':
						result.Append('\b');
					break;

					case 'f':
						result.Append('\f');
					break;

					case 'u':
						if (Position + 4 > Source.Length)
						{
							throw new FormatException("Invalid unicode escape in patch expression string.");
						}

						string hex = Source.Substring(Position, 4);

						if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int codePoint))
						{
							throw new FormatException("Invalid unicode escape in patch expression string.");
						}

						result.Append((char)codePoint);
						Position += 4;
					break;

					default:
						result.Append(escaped);
					break;
				}
			}

			throw new FormatException("Unterminated string in patch expression.");
		}

		private void SkipWhitespace()
		{
			while (Position < Source.Length && char.IsWhiteSpace(Source[Position])) { Position++; }
		}
	}

	private sealed class Parser
	{
		private readonly Lexer Lexer;
		private Token Current;

		public HashSet<string> SettingReferences { get; } = new(StringComparer.Ordinal);
		public HashSet<string> ConstantReferences { get; } = new(StringComparer.Ordinal);
		public HashSet<string> BareReferences { get; } = new(StringComparer.Ordinal);

		public Parser(string expression)
		{
			Lexer = new Lexer(expression);
			Current = Lexer.Next();
		}

		public ExpressionNode Parse()
		{
			ExpressionNode result = ParseConditional();
			Expect(TokenKind.End);
			return result;
		}

		private ExpressionNode ParseConditional()
		{
			ExpressionNode condition = ParseOr();

			if (!Take(TokenKind.Question)) { return condition; }

			ExpressionNode whenTrue = ParseConditional();
			Expect(TokenKind.Colon);
			ExpressionNode whenFalse = ParseConditional();

			return new ConditionalNode(condition, whenTrue, whenFalse);
		}

		private ExpressionNode ParseOr()
		{
			ExpressionNode left = ParseAnd();

			while (Take(TokenKind.OrOr)) { left = new BinaryNode(left, TokenKind.OrOr, ParseAnd()); }

			return left;
		}

		private ExpressionNode ParseAnd()
		{
			ExpressionNode left = ParseEquality();

			while (Take(TokenKind.AndAnd)) { left = new BinaryNode(left, TokenKind.AndAnd, ParseEquality()); }

			return left;
		}

		private ExpressionNode ParseEquality()
		{
			ExpressionNode left = ParseComparison();

			while (Current.Kind is TokenKind.EqualEqual or TokenKind.BangEqual)
			{
				TokenKind op = Current.Kind;
				Advance();

				left = new BinaryNode(left, op, ParseComparison());
			}

			return left;
		}

		private ExpressionNode ParseComparison()
		{
			ExpressionNode left = ParseAdditive();

			while (Current.Kind is TokenKind.Less or TokenKind.LessEqual or TokenKind.Greater or TokenKind.GreaterEqual)
			{
				TokenKind op = Current.Kind;
				Advance();

				left = new BinaryNode(left, op, ParseAdditive());
			}

			return left;
		}

		private ExpressionNode ParseAdditive()
		{
			ExpressionNode left = ParseMultiplicative();

			while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
			{
				TokenKind op = Current.Kind;
				Advance();

				left = new BinaryNode(left, op, ParseMultiplicative());
			}

			return left;
		}

		private ExpressionNode ParseMultiplicative()
		{
			ExpressionNode left = ParseUnary();

			while (Current.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent)
			{
				TokenKind op = Current.Kind;
				Advance();

				left = new BinaryNode(left, op, ParseUnary());
			}

			return left;
		}

		private ExpressionNode ParseUnary()
		{
			if (Current.Kind is TokenKind.Bang or TokenKind.Plus or TokenKind.Minus)
			{
				TokenKind op = Current.Kind;
				Advance();

				return new UnaryNode(op, ParseUnary());
			}

			return ParsePrimary();
		}

		private ExpressionNode ParsePrimary()
		{
			if (Current.Kind == TokenKind.Number)
			{
				double value = Current.Number;
				Advance();
				return new LiteralNode(new JValue(value));
			}

			if (Current.Kind == TokenKind.String)
			{
				string value = Current.Text;
				Advance();
				return new LiteralNode(new JValue(value));
			}

			if (Take(TokenKind.LeftParen))
			{
				ExpressionNode nested = ParseConditional();
				Expect(TokenKind.RightParen);
				return nested;
			}

			if (Current.Kind != TokenKind.Identifier)
			{
				throw new FormatException($"Expected expression value, found '{Current.Text}'.");
			}

			string identifier = Current.Text;
			Advance();

			if (Current.Kind == TokenKind.LeftParen) { return ParseFunction(identifier); }

			if (string.Equals(identifier, "true", StringComparison.OrdinalIgnoreCase)) { return new LiteralNode(new JValue(true)); }
			if (string.Equals(identifier, "false", StringComparison.OrdinalIgnoreCase)) { return new LiteralNode(new JValue(false)); }
			if (string.Equals(identifier, "null", StringComparison.OrdinalIgnoreCase)) { return new LiteralNode(JValue.CreateNull()); }
			if (string.Equals(identifier, "pi", StringComparison.OrdinalIgnoreCase)) { return new LiteralNode(new JValue(Math.PI)); }
			if (string.Equals(identifier, "e", StringComparison.OrdinalIgnoreCase)) { return new LiteralNode(new JValue(Math.E)); }
			if (string.Equals(identifier, "Setting", StringComparison.OrdinalIgnoreCase)) { return new ReferenceNode(ReferenceKind.Setting); }
			if (string.Equals(identifier, "Current", StringComparison.OrdinalIgnoreCase) || string.Equals(identifier, "value", StringComparison.OrdinalIgnoreCase)) { return new ReferenceNode(ReferenceKind.Current); }
			if (string.Equals(identifier, "Settings", StringComparison.OrdinalIgnoreCase))
			{
				string name = ParseNamedReference();
				SettingReferences.Add(name);

				return new ReferenceNode(ReferenceKind.NamedSetting, name);
			}

			if (string.Equals(identifier, "Constants", StringComparison.OrdinalIgnoreCase))
			{
				string name = ParseNamedReference();
				ConstantReferences.Add(name);

				return new ReferenceNode(ReferenceKind.NamedConstant, name);
			}

			BareReferences.Add(identifier);

			return new ReferenceNode(ReferenceKind.Bare, identifier);
		}

		private string ParseNamedReference()
		{
			if (Take(TokenKind.Dot))
			{
				if (Current.Kind != TokenKind.Identifier)
				{
					throw new FormatException("Expected an identifier after '.'.");
				}

				string name = Current.Text;
				Advance();
				return name;
			}

			if (Take(TokenKind.LeftBracket))
			{
				if (Current.Kind != TokenKind.String)
				{
					throw new FormatException("Bracketed setting/constant references require a quoted string.");
				}

				string name = Current.Text;
				Advance();
				Expect(TokenKind.RightBracket);
				return name;
			}

			throw new FormatException("Settings and Constants references require '.Name' or '[\"Name\"]'.");
		}

		private ExpressionNode ParseFunction(string name)
		{
			Expect(TokenKind.LeftParen);

			List<ExpressionNode> arguments = new();

			if (!Take(TokenKind.RightParen))
			{
				do { arguments.Add(ParseConditional()); }
				while (Take(TokenKind.Comma));

				Expect(TokenKind.RightParen);
			}

			string lower = name.ToLowerInvariant();

			if (lower is not ("sin" or "cos" or "abs" or "sqrt" or "ceiling" or "floor" or "exp" or "log" or "round" or "sign" or "clamp" or "max" or "min" or "greater" or "lesser" or "equal" or "notequal" or "if" or "not" or "and" or "or"))
			{
				throw new FormatException($"Unknown patch expression function '{name}'.");
			}

			ValidateFunctionArity(lower, arguments.Count);

			return new FunctionNode(name, arguments.ToArray());
		}

		private static void ValidateFunctionArity(string name, int count)
		{
			switch (name)
			{
				case "sin":
				case "cos":
				case "abs":
				case "sqrt":
				case "ceiling":
				case "floor":
				case "exp":
				case "sign":
				case "not":
					if (count != 1)
					{
						throw new FormatException($"{name}() requires 1 argument.");
					}

				return;

				case "log":
				case "round":
					if (count is not (1 or 2))
					{
						throw new FormatException($"{name}() requires one or two arguments.");
					}

				return;

				case "clamp":
				case "if":
					if (count != 3)
					{
						throw new FormatException($"{name}() requires 3 arguments.");
					}

				return;

				case "greater":
				case "lesser":
				case "equal":
				case "notequal":
					if (count != 4)
					{
						throw new FormatException($"{name}() requires 4 arguments.");
					}

				return;

				case "and":
				case "or":
					if (count < 2)
					{
						throw new FormatException($"{name}() requires at least two arguments.");
					}

				return;

				case "max":
				case "min":
					if (count < 1)
					{
						throw new FormatException($"{name}() requires at least one argument.");
					}

				return;
			}
		}

		private bool Take(TokenKind kind)
		{
			if (Current.Kind != kind) { return false; }

			Advance();
			return true;
		}

		private void Expect(TokenKind kind)
		{
			if (Current.Kind != kind)
			{
				throw new FormatException($"Expected {kind}, found '{Current.Text}'.");
			}

			Advance();
		}

		private void Advance() { Current = Lexer.Next(); }
	}
}
