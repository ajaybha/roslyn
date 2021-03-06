﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.Shared.Extensions;

namespace Microsoft.CodeAnalysis.DocumentationCommentFormatting
{
    internal abstract class AbstractDocumentationCommentFormattingService : IDocumentationCommentFormattingService
    {
        private int _position;
        private SemanticModel _semanticModel;

        private class FormatterState
        {
            private bool _anyNonWhitespaceSinceLastPara;
            private bool _pendingParagraphBreak;
            private bool _pendingSingleSpace;

            private static SymbolDisplayPart s_spacePart = new SymbolDisplayPart(SymbolDisplayPartKind.Space, null, " ");
            private static SymbolDisplayPart s_newlinePart = new SymbolDisplayPart(SymbolDisplayPartKind.LineBreak, null, "\r\n");

            internal readonly List<SymbolDisplayPart> Builder = new List<SymbolDisplayPart>();

            internal SemanticModel SemanticModel { get; set; }
            internal int Position { get; set; }

            public bool AtBeginning
            {
                get
                {
                    return Builder.Count == 0;
                }
            }

            public SymbolDisplayFormat Format { get; internal set; }

            public void AppendSingleSpace()
            {
                _pendingSingleSpace = true;
            }

            public void AppendString(string s)
            {
                EmitPendingChars();

                Builder.Add(new SymbolDisplayPart(SymbolDisplayPartKind.Text, null, s));

                _anyNonWhitespaceSinceLastPara = true;
            }

            public void AppendParts(IEnumerable<SymbolDisplayPart> parts)
            {
                EmitPendingChars();

                Builder.AddRange(parts);

                _anyNonWhitespaceSinceLastPara = true;
            }

            public bool TryAppendSymbol(ISymbol symbol)
            {
                if (symbol == null)
                {
                    return false;
                }

                var format = Format;
                if (symbol.IsConstructor())
                {
                    format = format.WithMemberOptions(SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeExplicitInterface);
                }

                var parts = SemanticModel != null
                    ? symbol.ToMinimalDisplayParts(SemanticModel, Position, format)
                    : symbol.ToDisplayParts(format);

                AppendParts(parts);

                return true;
            }

            public void MarkBeginOrEndPara()
            {
                // If this is a <para> with nothing before it, then skip it.
                if (_anyNonWhitespaceSinceLastPara == false)
                {
                    return;
                }

                _pendingParagraphBreak = true;

                // Reset flag.
                _anyNonWhitespaceSinceLastPara = false;
            }

            public string GetText()
            {
                return Builder.GetFullText();
            }

            private void EmitPendingChars()
            {
                if (_pendingParagraphBreak)
                {
                    Builder.Add(s_newlinePart);
                    Builder.Add(s_newlinePart);
                }
                else if (_pendingSingleSpace)
                {
                    Builder.Add(s_spacePart);
                }

                _pendingParagraphBreak = false;
                _pendingSingleSpace = false;
            }
        }

        public string Format(string rawXmlText, Compilation compilation = null)
        {
            if (rawXmlText == null)
            {
                return null;
            }

            var state = new FormatterState();

            // In case the XML is a fragment (that is, a series of elements without a parent)
            // wrap it up in a single tag. This makes parsing it much, much easier.
            var inputString = "<tag>" + rawXmlText + "</tag>";

            var summaryElement = XElement.Parse(inputString, LoadOptions.PreserveWhitespace);

            AppendTextFromNode(state, summaryElement, compilation);

            return state.GetText();
        }

        public IEnumerable<SymbolDisplayPart> Format(string rawXmlText, SemanticModel semanticModel, int position, SymbolDisplayFormat format = null)
        {
            if (rawXmlText == null)
            {
                return null;
            }

            var state = new FormatterState() { SemanticModel = semanticModel, Position = position, Format = format };

            // In case the XML is a fragment (that is, a series of elements without a parent)
            // wrap it up in a single tag. This makes parsing it much, much easier.
            var inputString = "<tag>" + rawXmlText + "</tag>";

            var summaryElement = XElement.Parse(inputString, LoadOptions.PreserveWhitespace);

            AppendTextFromNode(state, summaryElement, state.SemanticModel.Compilation);

            return state.Builder;
        }

        private static void AppendTextFromNode(FormatterState state, XNode node, Compilation compilation)
        {
            if (node.NodeType == XmlNodeType.Text)
            {
                AppendTextFromTextNode(state, (XText)node);
            }

            if (node.NodeType != XmlNodeType.Element)
            {
                return;
            }

            var element = (XElement)node;

            var name = element.Name.LocalName;

            if (name == "see" ||
                name == "seealso")
            {
                AppendTextFromSeeTag(state, element, compilation);
                return;
            }
            else if (name == "paramref" ||
                    name == "typeparamref")
            {
                AppendTextFromTagWithNameAttribute(state, element, compilation);
                return;
            }

            if (name == "para")
            {
                state.MarkBeginOrEndPara();
            }

            foreach (var childNode in element.Nodes())
            {
                AppendTextFromNode(state, childNode, compilation);
            }

            if (name == "para")
            {
                state.MarkBeginOrEndPara();
            }
        }

        private static void AppendTextFromTagWithNameAttribute(FormatterState state, XElement element, Compilation compilation)
        {
            var nameAttribute = element.Attribute("name");

            if (nameAttribute == null)
            {
                return;
            }

            if (compilation != null && state.TryAppendSymbol(DocumentationCommentId.GetFirstSymbolForDeclarationId(nameAttribute.Value, compilation)))
            {
                return;
            }
            else
            {
                state.AppendString(TrimCrefPrefix(nameAttribute.Value));
            }
        }

        private static void AppendTextFromSeeTag(FormatterState state, XElement element, Compilation compilation)
        {
            var crefAttribute = element.Attribute("cref");

            if (crefAttribute == null)
            {
                return;
            }

            var crefValue = crefAttribute.Value;

            if (compilation != null && state.TryAppendSymbol(DocumentationCommentId.GetFirstSymbolForDeclarationId(crefValue, compilation)))
            {
                return;
            }
            else
            {
                state.AppendString(TrimCrefPrefix(crefValue));
            }
        }

        private static string TrimCrefPrefix(string value)
        {
            if (value.Length >= 2 && value[1] == ':')
            {
                value = value.Substring(startIndex: 2);
            }

            return value;
        }

        private static void AppendTextFromTextNode(FormatterState state, XText element)
        {
            var rawText = element.Value;
            var builder = new StringBuilder(rawText.Length);

            // Normalize the whitespace.
            var pendingWhitespace = false;
            var hadAnyNonWhitespace = false;
            for (int i = 0; i < rawText.Length; i++)
            {
                if (char.IsWhiteSpace(rawText[i]))
                {
                    // Whitespace. If it occurs at the beginning of the text we don't append it
                    // at all; otherwise, we reduce it to a single space.
                    if (!state.AtBeginning || hadAnyNonWhitespace)
                    {
                        pendingWhitespace = true;
                    }
                }
                else
                {
                    // Some other character...
                    if (pendingWhitespace)
                    {
                        if (builder.Length == 0)
                        {
                            state.AppendSingleSpace();
                        }
                        else
                        {
                            builder.Append(' ');
                        }

                        pendingWhitespace = false;
                    }

                    builder.Append(rawText[i]);
                    hadAnyNonWhitespace = true;
                }
            }

            if (builder.Length > 0)
            {
                state.AppendString(builder.ToString());
            }

            if (pendingWhitespace)
            {
                state.AppendSingleSpace();
            }
        }
    }
}
