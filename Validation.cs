using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace XisfLib.Core.Implementations
{
    /// <summary>
    /// Validator for XISF structures and identifiers.
    /// Specification Reference: Section 8.4.1 Property Identifier, Section 11 Core Elements
    /// </summary>
    internal sealed class XisfValidator : IXisfValidator
    {
        // Property identifier regex per specification: [_a-zA-Z][_a-zA-Z0-9]*(::[_a-zA-Z][_a-zA-Z0-9]*)*
        private static readonly Regex PropertyIdRegex = new(
            @"^[_a-zA-Z][_a-zA-Z0-9]*(::[_a-zA-Z][_a-zA-Z0-9]*)*$",
            RegexOptions.Compiled);

        // Unique element identifier regex: [_a-zA-Z][_a-zA-Z0-9]*
        private static readonly Regex UniqueIdRegex = new(
            @"^[_a-zA-Z][_a-zA-Z0-9]*$",
            RegexOptions.Compiled);

        /// <summary>
        /// Validates an XISF header structure.
        /// </summary>
        public ValidationResult ValidateHeader(XisfHeader header)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            if (header == null)
            {
                errors.Add("Header cannot be null");
                return new ValidationResult(false, errors, warnings);
            }

            // Validate metadata
            if (header.Metadata == null)
            {
                errors.Add("Header must contain metadata");
            }
            else
            {
                if (string.IsNullOrEmpty(header.Metadata.CreatorApplication))
                {
                    errors.Add("Metadata must specify creator application");
                }
            }

            // Validate unique IDs in core elements
            if (header.CoreElements != null)
            {
                var seenIds = new HashSet<string>();
                foreach (var kvp in header.CoreElements)
                {
                    if (!IsValidUniqueId(kvp.Key))
                    {
                        errors.Add($"Invalid unique element ID: {kvp.Key}");
                    }

                    if (seenIds.Contains(kvp.Key))
                    {
                        errors.Add($"Duplicate unique element ID: {kvp.Key}");
                    }
                    seenIds.Add(kvp.Key);
                }
            }

            return new ValidationResult(errors.Count == 0, errors, warnings);
        }

        /// <summary>
        /// Validates an XISF unit structure.
        /// </summary>
        public ValidationResult ValidateUnit(XisfUnit unit)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            if (unit == null)
            {
                errors.Add("Unit cannot be null");
                return new ValidationResult(false, errors, warnings);
            }

            // Validate header
            var headerResult = ValidateHeader(unit.Header);
            errors.AddRange(headerResult.Errors);
            warnings.AddRange(headerResult.Warnings);

            // Validate storage model
            if (unit.StorageModel == null)
            {
                warnings.Add("Storage model not specified");
            }

            return new ValidationResult(errors.Count == 0, errors, warnings);
        }

        /// <summary>
        /// Validates an XISF image structure.
        /// Specification Reference: Section 8.5 XISF Image
        /// </summary>
        public ValidationResult ValidateImage(XisfImage image)
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            if (image == null)
            {
                errors.Add("Image cannot be null");
                return new ValidationResult(false, errors, warnings);
            }

            // Validate geometry
            if (image.Geometry == null)
            {
                errors.Add("Image must have geometry");
            }
            else if (image.Geometry.ChannelCount == 0)
            {
                errors.Add("Image must have at least one channel");
            }

            // Validate bounds for floating point images
            if (image.SampleFormat == XisfSampleFormat.Float32 ||
                image.SampleFormat == XisfSampleFormat.Float64 ||
                image.SampleFormat == XisfSampleFormat.Complex32 ||
                image.SampleFormat == XisfSampleFormat.Complex64)
            {
                if (image.Bounds == null)
                {
                    warnings.Add("Floating point images should specify bounds");
                }
            }

            // Check color space and channel count consistency
            if (image.ColorSpace == XisfColorSpace.RGB && image.Geometry?.ChannelCount < 3)
            {
                warnings.Add("RGB color space typically requires at least 3 channels");
            }

            // Validate associated properties
            if (image.Properties != null)
            {
                foreach (var property in image.Properties)
                {
                    if (!IsValidPropertyId(property.Id))
                    {
                        errors.Add($"Invalid property identifier in image: {property.Id}");
                    }
                }
            }

            return new ValidationResult(errors.Count == 0, errors, warnings);
        }

        /// <summary>
        /// Validates a property identifier format.
        /// Specification Reference: Section 8.4.1 Property Identifier
        /// </summary>
        public bool IsValidPropertyId(string id)
        {
            if (string.IsNullOrEmpty(id))
                return false;

            return PropertyIdRegex.IsMatch(id);
        }

        /// <summary>
        /// Validates a unique element identifier format.
        /// Specification Reference: Section 11 XISF Core Elements
        /// </summary>
        public bool IsValidUniqueId(string uid)
        {
            if (string.IsNullOrEmpty(uid))
                return false;

            return UniqueIdRegex.IsMatch(uid);
        }
    }
}
