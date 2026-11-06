namespace Larva.Hir
{
    /// <summary>
    /// Visits every statement, expression and pattern of a body, children after their parent. Override a <c>Visit</c>
    /// method to look at nodes (call the base to go on into the children, or skip it to stop there).
    /// </summary>
    public abstract class HirWalker
    {
        public virtual void Visit(Stmt? s)
        {
            switch (s)
            {
                case null:
                    return;
                case BlockStmt block:
                    foreach (var statement in block.Statements)
                        Visit(statement);
                    return;
                case LocalStmt local:
                    Visit(local.Value);
                    return;
                case ExprStmt e:
                    Visit(e.Expression);
                    return;
                case IfStmt i:
                    Visit(i.Condition);
                    Visit(i.Then);
                    Visit(i.Else);
                    return;
                case WhileStmt w:
                    Visit(w.Condition);
                    Visit(w.Body);
                    return;
                case DoWhileStmt d:
                    Visit(d.Body);
                    Visit(d.Condition);
                    return;
                case ForStmt f:
                    foreach (var initializer in f.Initializers)
                        Visit(initializer);
                    Visit(f.Condition);
                    foreach (var step in f.Steps)
                        Visit(step);
                    Visit(f.Body);
                    return;
                case ForeachStmt f:
                    Visit(f.Collection);
                    Visit(f.Body);
                    return;
                case SwitchStmt sw:
                    Visit(sw.Value);
                    foreach (var section in sw.Sections)
                    {
                        foreach (var pattern in section.Patterns)
                            Visit(pattern);
                        Visit(section.Guard);
                        Visit(section.Body);
                    }
                    return;
                case ReturnStmt r:
                    Visit(r.Value);
                    return;
                case ThrowStmt t:
                    Visit(t.Value);
                    return;
                case TryStmt t:
                    Visit(t.Body);
                    foreach (var clause in t.Catches)
                    {
                        Visit(clause.Filter);
                        Visit(clause.Body);
                    }
                    Visit(t.Finally);
                    return;
                case UsingStmt u:
                    Visit(u.Resource);
                    Visit(u.Body);
                    return;
                case DeleteStmt d:
                    Visit(d.Value);
                    return;
                case RegionStmt r:
                    Visit(r.Body);
                    return;
                case UnsafeStmt u:
                    Visit(u.Body);
                    return;
                case YieldStmt y:
                    Visit(y.Value);
                    return;
                case CheckedStmt c:
                    Visit(c.Body);
                    return;
            }
        }

        private void Visit(Expr[] expressions)
        {
            foreach (var e in expressions)
                Visit(e);
        }

        public virtual void Visit(Expr? e)
        {
            switch (e)
            {
                case null:
                    return;
                case LambdaExpr lambda:
                    Visit(lambda.Body);
                    return;
                case FieldExpr f:
                    Visit(f.Instance);
                    return;
                case PropertyExpr p:
                    Visit(p.Instance);
                    Visit(p.Indices);
                    return;
                case ElementExpr x:
                    Visit(x.Array);
                    Visit(x.Indices);
                    return;
                case CallExpr call:
                    Visit(call.Receiver);
                    Visit(call.Arguments);
                    return;
                case InvokeExpr i:
                    Visit(i.Function);
                    Visit(i.Arguments);
                    return;
                case NewExpr n:
                    Visit(n.Arguments);
                    foreach (var init in n.Initializers)
                        Visit(init.Value);
                    return;
                case NewArrayExpr n:
                    Visit(n.Lengths);
                    return;
                case ArrayLiteralExpr a:
                    Visit(a.Items);
                    return;
                case UnaryExpr u:
                    Visit(u.Operand);
                    return;
                case BinaryExpr bin:
                    Visit(bin.Left);
                    Visit(bin.Right);
                    return;
                case AssignExpr a:
                    Visit(a.Target);
                    Visit(a.Value);
                    return;
                case ConvertExpr cv:
                    Visit(cv.Value);
                    return;
                case IsExpr i:
                    Visit(i.Value);
                    Visit(i.Pattern);
                    return;
                case AsExpr a:
                    Visit(a.Value);
                    return;
                case SwitchExpr sw:
                    Visit(sw.Value);
                    foreach (var arm in sw.Arms)
                    {
                        Visit(arm.Pattern);
                        Visit(arm.Guard);
                        Visit(arm.Value);
                    }
                    return;
                case ConditionalExpr ce:
                    Visit(ce.Condition);
                    Visit(ce.Then);
                    Visit(ce.Else);
                    return;
                case CoalesceExpr co:
                    Visit(co.Left);
                    Visit(co.Right);
                    return;
                case ConditionalAccessExpr ca:
                    Visit(ca.Receiver);
                    Visit(ca.Access);
                    return;
                case MethodRefExpr m:
                    Visit(m.Target);
                    return;
                case AwaitExpr a:
                    Visit(a.Task);
                    return;
                case CheckedExpr c:
                    Visit(c.Value);
                    return;
                case ArrayLengthExpr a:
                    Visit(a.Array);
                    Visit(a.Dimension);
                    return;
                case WithExpr w:
                    Visit(w.Value);
                    foreach (var change in w.Changes)
                        Visit(change.Value);
                    return;
                case RangeExpr r:
                    Visit(r.Low);
                    Visit(r.High);
                    return;
                case InterpolatedExpr i:
                    foreach (var part in i.Parts)
                        Visit(part.Value);
                    return;
                case BorrowExpr bw:
                    Visit(bw.Place);
                    return;
                case AddressOfExpr ad:
                    Visit(ad.Place);
                    return;
                case DerefExpr d:
                    Visit(d.Pointer);
                    return;
                case BlockExpr block:
                    foreach (var statement in block.Statements)
                        Visit(statement);
                    Visit(block.Value);
                    return;
            }
        }

        public virtual void Visit(Pattern? p)
        {
            switch (p)
            {
                case BindingPattern bind:
                    Visit(bind.Inner);
                    return;
                case AndPattern a:
                    Visit(a.Left);
                    Visit(a.Right);
                    return;
                case OrPattern o:
                    Visit(o.Left);
                    Visit(o.Right);
                    return;
                case NotPattern n:
                    Visit(n.Operand);
                    return;
                case PropertyPattern pp:
                    foreach (var member in pp.Members)
                        Visit(member.Pattern);
                    return;
                case PositionalPattern pos:
                    foreach (var item in pos.Items)
                        Visit(item);
                    return;
                case ListPattern list:
                    foreach (var item in list.Items)
                        Visit(item);
                    return;
            }
        }
    }
}
