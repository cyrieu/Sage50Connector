// Creates clearly tagged invoices only in the explicitly selected lab test company.
using Newtonsoft.Json;
using Sage.Peachtree.API;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Sage50Connector.Diagnostics
{
    internal static class InvoiceSeedingDiagnostic
    {
        private static EntityReference<TEntity> Ref<TEntity>(object key) where TEntity : Entity
        {
            var keyType = key.GetType();
            object guid = keyType.GetProperty("Guid").GetValue(key, null);
            return EntityReference.Create<TEntity>((Guid)guid);
        }

        private static string[] ValidationMessages(ValidationProblemList problems)
        {
            var messages = new List<string>();
            foreach (object problem in problems)
            {
                object message = problem.GetType().GetProperty("Message")?.GetValue(problem, null);
                messages.Add(message == null ? problem.ToString() : message.ToString());
            }
            return messages.ToArray();
        }

        private static int Run(string companyName, int startNumber, int count, string manifestPath, DateTime? candidateDate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(manifestPath)));
            var errors = new List<object>();
            int createdCount = 0;
            int skippedCount = 0;
            var startedUtc = DateTime.UtcNow;
            try
            {
                if (!string.Equals(companyName, "Rutter Test Co", StringComparison.Ordinal))
                    throw new InvalidOperationException("Invoice seeding is restricted to the owned Rutter Test Co lab company.");
                if (startNumber < 1 || count < 0 || startNumber + (long)count > int.MaxValue)
                    throw new ArgumentOutOfRangeException("count", "Start number must be positive; count must be nonnegative and fit in a 32-bit invoice reference range.");
                if (count > 0 && !candidateDate.HasValue)
                    throw new ArgumentException("Invoice seeding requires an explicit candidate date in a confirmed open period.");
                Program.CompanyName = companyName;
                string open = Helpers.Sage50Repository.Instance.OpenCompany(companyName);
                if (Helpers.CompanyManager.Instance.CurrentCompany == null)
                {
                    File.WriteAllText(manifestPath, JsonConvert.SerializeObject(new { status = "company-open-failed", companyName, result = open }, Formatting.Indented));
                    return 2;
                }

                var company = Helpers.CompanyManager.Instance.CurrentCompany;
                var factories = company.Factories;
                Account receivable = null, income = null;
                var accounts = factories.AccountFactory.List(); accounts.Load();
                foreach (Account account in accounts)
                {
                    if (receivable == null && account.Classification == AccountClassification.Receivable) receivable = account;
                    if (income == null && account.Classification == AccountClassification.Income) income = account;
                }
                if (receivable == null || income == null)
                    throw new InvalidOperationException("Could not find both a receivable and income account in the dedicated test company.");

                Customer customer = null;
                var customers = factories.CustomerFactory.List(); customers.Load();
                foreach (Customer candidate in customers)
                    if (string.Equals(candidate.ID, "RUTMEMTEST", StringComparison.Ordinal)) customer = candidate;
                bool customerCreated = false;
                if (customer == null)
                {
                    customer = factories.CustomerFactory.Create();
                    customer.ID = "RUTMEMTEST";
                    customer.Name = "Rutter Invoice Memory Test";
                    customerCreated = true;
                }
                else if (!string.Equals(customer.Name, "Rutter Invoice Memory Test", StringComparison.Ordinal))
                    throw new InvalidOperationException("Customer ID RUTMEMTEST is already used by a differently named customer; refusing to modify it.");
                // Synthetic fixture only: preserve payment settings while giving the
                // test customer enough headroom for 60K invoices (~$20M).
                var terms = customer.Terms;
                customer.Terms = factories.PaymentTermsFactory.Create(
                    terms.PaymentTimeFrame, terms.DueDays, terms.UseDiscounts,
                    terms.DiscountDays, terms.DiscountPercent, 100000000m, terms.ChargeInterest);
                customer.UsualSalesAccountReference = Ref<Account>(income.Key);
                Contact contact = customer.BillToContact ?? customer.AddContact();
                if (string.IsNullOrEmpty(contact.CompanyName)) contact.CompanyName = "Rutter Invoice Memory Test";
                var customerProblems = new ValidationProblemList();
                customer.Validate(customerProblems);
                if (customerProblems.Count != 0)
                    throw new InvalidOperationException("Customer validation failed: " + string.Join(" | ", ValidationMessages(customerProblems)));
                customer.Save();

                var periods = company.Defaults.GeneralLedger.AccountingPeriods.Cast<AccountingPeriod>()
                    .Where(period => period.From != default(DateTime) && period.To != default(DateTime))
                    .OrderBy(period => period.From).ToList();
                if (periods.Count == 0) throw new InvalidOperationException("Company has no configured fiscal accounting periods.");
                DateTime fiscalStart = periods.Min(p => p.From).Date;
                DateTime fiscalEnd = periods.Max(p => p.To).Date;
                if (count == 0)
                {
                    var periodRows = periods.Select(period => (object)new
                    {
                        from = period.From.ToString("o", CultureInfo.InvariantCulture),
                        fromKind = period.From.Kind.ToString(),
                        to = period.To.ToString("o", CultureInfo.InvariantCulture),
                        toKind = period.To.Kind.ToString(),
                        publicProperties = period.GetType().GetProperties()
                            .Where(property => property.GetIndexParameters().Length == 0)
                            .Select(property => new
                            {
                                name = property.Name,
                                type = property.PropertyType.FullName,
                                value = Convert.ToString(property.GetValue(period, null), CultureInfo.InvariantCulture)
                            })
                    }).ToArray();
                    File.WriteAllText(manifestPath, JsonConvert.SerializeObject(new
                    {
                        status = "period-inspection", companyName, fiscalStart = fiscalStart.ToString("o", CultureInfo.InvariantCulture),
                        fiscalEnd = fiscalEnd.ToString("o", CultureInfo.InvariantCulture), periodRows
                    }, Formatting.Indented));
                    return 0;
                }
                var invoiceFactory = factories.SalesInvoiceFactory;
                var existingInvoices = invoiceFactory.List(); existingInvoices.Load();
                var existingReferences = new HashSet<string>(StringComparer.Ordinal);
                foreach (SalesInvoice existing in existingInvoices)
                    if (!string.IsNullOrEmpty(existing.ReferenceNumber)) existingReferences.Add(existing.ReferenceNumber);
                string rowsPath = manifestPath + ".rows.jsonl";
                // Append on resume; existing Sage references are skipped, never overwritten.
                DateTime seedMonthStart = candidateDate.Value.Date.AddDays(1 - candidateDate.Value.Day);
                DateTime seedMonthEnd = seedMonthStart.AddMonths(1).AddDays(-1);
                using (var rows = new StreamWriter(rowsPath, true))
                {
                    rows.AutoFlush = true;
                    rows.WriteLine(JsonConvert.SerializeObject(new { kind = "seed-header", companyName, startNumber, count, customerId = customer.ID, receivableAccountId = receivable.ID, incomeAccountId = income.ID, fiscalStart = fiscalStart.ToString("yyyy-MM-dd"), fiscalEnd = fiscalEnd.ToString("yyyy-MM-dd"), seedDateStart = seedMonthStart.ToString("yyyy-MM-dd"), seedDateEnd = seedMonthEnd.ToString("yyyy-MM-dd") }));
                for (int offset = 0; offset < count; offset++)
                {
                    int number = startNumber + offset;
                    string reference = "MEM" + number.ToString("D8", CultureInfo.InvariantCulture);
                    if (existingReferences.Contains(reference))
                    {
                        skippedCount++;
                        continue;
                    }
                    DateTime date;
                    int daysInMonth = (seedMonthEnd - seedMonthStart).Days + 1;
                    if (offset == 0) date = candidateDate.Value.Date;
                    else if (offset == 1) date = seedMonthStart;
                    else if (offset == 2) date = seedMonthStart.AddDays(1);
                    else if (offset == 3) date = seedMonthEnd.AddDays(-1);
                    else if (offset == 4) date = seedMonthEnd;
                    else date = seedMonthStart.AddDays((offset * 37L) % daysInMonth);

                    int lineCount = 5 + (number % 16);
                    var invoice = invoiceFactory.Create();
                    invoice.ReferenceNumber = "MEM" + number.ToString("D8", CultureInfo.InvariantCulture);
                    invoice.Date = date;
                    invoice.CustomerReference = Ref<Customer>(customer.Key);
                    invoice.AccountReference = Ref<Account>(receivable.Key);
                    for (int lineNumber = 1; lineNumber <= lineCount; lineNumber++)
                    {
                        var line = invoice.AddSalesLine();
                        line.Quantity = 1m + (lineNumber % 5);
                        line.UnitPrice = 1m + (lineNumber % 23);
                        line.Description = "RUTMEM synthetic invoice " + number.ToString("D8", CultureInfo.InvariantCulture) + " line " + lineNumber.ToString("D2", CultureInfo.InvariantCulture);
                        line.AccountReference = Ref<Account>(income.Key);
                        line.Amount = line.CalculateAmount(line.Quantity, line.UnitPrice);
                    }
                    var problems = new ValidationProblemList(); invoice.Validate(problems);
                    if (problems.Count != 0)
                        throw new InvalidOperationException("Invoice " + invoice.ReferenceNumber + " validation failed: " + string.Join(" | ", ValidationMessages(problems)));
                    invoice.Save();
                    rows.WriteLine(JsonConvert.SerializeObject(new { kind = "invoice", id = invoice.Key.Guid.ToString(), referenceNumber = invoice.ReferenceNumber, date = date.ToString("yyyy-MM-dd"), usedLines = lineCount }));
                    createdCount++;
                    if (createdCount == 1 || (createdCount % 100) == 0)
                    {
                        File.WriteAllText(manifestPath, JsonConvert.SerializeObject(new
                        {
                            status = "seeding", companyName, requestedStart = startNumber, requestedCount = count,
                            createdCount, skippedCount, processedCount = createdCount + skippedCount, startedUtc, updatedUtc = DateTime.UtcNow, nextNumber = number + 1, customerCreated, customerId = customer.ID,
                            receivableAccountId = receivable.ID, incomeAccountId = income.ID,
                            fiscalStart = fiscalStart.ToString("yyyy-MM-dd"), fiscalEnd = fiscalEnd.ToString("yyyy-MM-dd"),
                            seedDateStart = seedMonthStart.ToString("yyyy-MM-dd"), seedDateEnd = seedMonthEnd.ToString("yyyy-MM-dd"), rowsPath, errors
                        }, Formatting.Indented));
                    }
                }
                rows.WriteLine(JsonConvert.SerializeObject(new { kind = "seed-summary", status = "completed", createdCount }));
                }
                File.WriteAllText(manifestPath, JsonConvert.SerializeObject(new
                {
                    status = "completed", companyName, requestedStart = startNumber, requestedCount = count,
                    createdCount, skippedCount, processedCount = createdCount + skippedCount, startedUtc, updatedUtc = DateTime.UtcNow, nextNumber = startNumber + count, customerCreated, customerId = customer.ID,
                    receivableAccountId = receivable.ID, incomeAccountId = income.ID,
                    fiscalStart = fiscalStart.ToString("yyyy-MM-dd"), fiscalEnd = fiscalEnd.ToString("yyyy-MM-dd"),
                    seedDateStart = seedMonthStart.ToString("yyyy-MM-dd"), seedDateEnd = seedMonthEnd.ToString("yyyy-MM-dd"), rowsPath, errors
                }, Formatting.Indented));
                return 0;
            }
            catch (Exception ex)
            {
                while (ex is System.Reflection.TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
                errors.Add(ex.ToString());
                File.WriteAllText(manifestPath, JsonConvert.SerializeObject(new { status = "failed", companyName, requestedStart = startNumber, requestedCount = count, createdCount, skippedCount, startedUtc, updatedUtc = DateTime.UtcNow, errors }, Formatting.Indented));
                return 1;
            }
            finally { Helpers.Sage50Connector.Instance.Shutdown(); }
        }

        public static int Run(string[] args)
        {
            if (args.Length != 5 && args.Length != 6) throw new ArgumentException("Usage: --seed-invoices <company> <start-number> <count> <manifest.json> [first-invoice-date]");
            DateTime? candidateDate = args.Length == 6 ? DateTime.Parse(args[5], CultureInfo.InvariantCulture) : (DateTime?)null;
            return Run(args[1], int.Parse(args[2], CultureInfo.InvariantCulture), int.Parse(args[3], CultureInfo.InvariantCulture), args[4], candidateDate);
        }
    }
}
