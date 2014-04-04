﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web.Mvc;
using SmartStore.Core.Domain.Orders;
using SmartStore.Core.Domain.Payments;
using SmartStore.Plugin.Payments.PayPalDirect.Models;
using SmartStore.Plugin.Payments.PayPalDirect.Validators;
using SmartStore.Services.Configuration;
using SmartStore.Services.Localization;
using SmartStore.Services.Logging;
using SmartStore.Services.Orders;
using SmartStore.Services.Payments;
using SmartStore.Web.Framework.Controllers;
using SmartStore.Web.Framework.Plugins;

namespace SmartStore.Plugin.Payments.PayPalDirect.Controllers
{
	public class PaymentPayPalDirectController : PaymentControllerBase
	{
		private readonly PluginHelperBase _helper;
		private readonly ISettingService _settingService;
		private readonly IPaymentService _paymentService;
		private readonly IOrderService _orderService;
		private readonly IOrderProcessingService _orderProcessingService;
		private readonly ILogger _logger;
		private readonly PayPalDirectPaymentSettings _paypalDirectPaymentSettings;
		private readonly PaymentSettings _paymentSettings;
		private readonly ILocalizationService _localizationService;

		public PaymentPayPalDirectController(ISettingService settingService,
			IPaymentService paymentService, IOrderService orderService,
			IOrderProcessingService orderProcessingService,
			ILogger logger, PayPalDirectPaymentSettings paypalDirectPaymentSettings,
			PaymentSettings paymentSettings, ILocalizationService localizationService)
		{
			this._settingService = settingService;
			this._paymentService = paymentService;
			this._orderService = orderService;
			this._orderProcessingService = orderProcessingService;
			this._logger = logger;
			this._paypalDirectPaymentSettings = paypalDirectPaymentSettings;
			this._paymentSettings = paymentSettings;
			this._localizationService = localizationService;

			_helper = new PluginHelperBase("Payments.PayPalDirect");	// codehint: sm-add
		}

		// codehint: sm-add
		private SelectList TransactModeValues(TransactMode selected) {
			return new SelectList(new List<object>() {
				new { ID = (int)TransactMode.Authorize, Name = _helper.Resource("ModeAuth") },
				new { ID = (int)TransactMode.AuthorizeAndCapture, Name = _helper.Resource("ModeAuthAndCapture") }
			}, "ID", "Name", (int)selected);
		}

		[AdminAuthorize]
		[ChildActionOnly]
		public ActionResult Configure() 
		{
			var model = new ConfigurationModel();
			model.UseSandbox = _paypalDirectPaymentSettings.UseSandbox;
			model.TransactMode = Convert.ToInt32(_paypalDirectPaymentSettings.TransactMode);
			model.ApiAccountName = _paypalDirectPaymentSettings.ApiAccountName;
			model.ApiAccountPassword = _paypalDirectPaymentSettings.ApiAccountPassword;
			model.Signature = _paypalDirectPaymentSettings.Signature;
			model.AdditionalFee = _paypalDirectPaymentSettings.AdditionalFee;
			model.AdditionalFeePercentage = _paypalDirectPaymentSettings.AdditionalFeePercentage;

			// codehint: sm-edit
			//model.TransactModeValues = _paypalDirectPaymentSettings.TransactMode.ToSelectList();
			model.TransactModeValues = TransactModeValues(_paypalDirectPaymentSettings.TransactMode);

			return View("SmartStore.Plugin.Payments.PayPalDirect.Views.PaymentPayPalDirect.Configure", model);
		}

		[HttpPost]
		[AdminAuthorize]
		[ChildActionOnly]
		public ActionResult Configure(ConfigurationModel model, FormCollection form)
		{
			if (!ModelState.IsValid)
				return Configure();

			//save settings
			_paypalDirectPaymentSettings.UseSandbox = model.UseSandbox;
			_paypalDirectPaymentSettings.TransactMode = (TransactMode)model.TransactMode;
			_paypalDirectPaymentSettings.ApiAccountName = model.ApiAccountName;
			_paypalDirectPaymentSettings.ApiAccountPassword = model.ApiAccountPassword;
			_paypalDirectPaymentSettings.Signature = model.Signature;
			_paypalDirectPaymentSettings.AdditionalFee = model.AdditionalFee;
			_paypalDirectPaymentSettings.AdditionalFeePercentage = model.AdditionalFeePercentage;
			_settingService.SaveSetting(_paypalDirectPaymentSettings);

			// codehint: sm-edit
			//model.TransactModeValues = _paypalDirectPaymentSettings.TransactMode.ToSelectList();
			model.TransactModeValues = TransactModeValues(_paypalDirectPaymentSettings.TransactMode);

			return View("SmartStore.Plugin.Payments.PayPalDirect.Views.PaymentPayPalDirect.Configure", model);
		}

		[ChildActionOnly]
		public ActionResult PaymentInfo() {
			var model = new PaymentInfoModel();

			//CC types
			model.CreditCardTypes.Add(new SelectListItem() {
				Text = "Visa",
				Value = "Visa",
			});
			model.CreditCardTypes.Add(new SelectListItem() {
				Text = "Master card",
				Value = "MasterCard",
			});
			model.CreditCardTypes.Add(new SelectListItem() {
				Text = "Discover",
				Value = "Discover",
			});
			model.CreditCardTypes.Add(new SelectListItem() {
				Text = "Amex",
				Value = "Amex",
			});

			//years
			for (int i = 0; i < 15; i++) {
				string year = Convert.ToString(DateTime.Now.Year + i);
				model.ExpireYears.Add(new SelectListItem() {
					Text = year,
					Value = year,
				});
			}

			//months
			for (int i = 1; i <= 12; i++) {
				string text = (i < 10) ? "0" + i.ToString() : i.ToString();
				model.ExpireMonths.Add(new SelectListItem() {
					Text = text,
					Value = i.ToString(),
				});
			}

			//set postback values
			var form = this.Request.Form;
			model.CardholderName = form["CardholderName"];
			model.CardNumber = form["CardNumber"];
			model.CardCode = form["CardCode"];
			var selectedCcType = model.CreditCardTypes.Where(x => x.Value.Equals(form["CreditCardType"], StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
			if (selectedCcType != null)
				selectedCcType.Selected = true;
			var selectedMonth = model.ExpireMonths.Where(x => x.Value.Equals(form["ExpireMonth"], StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
			if (selectedMonth != null)
				selectedMonth.Selected = true;
			var selectedYear = model.ExpireYears.Where(x => x.Value.Equals(form["ExpireYear"], StringComparison.InvariantCultureIgnoreCase)).FirstOrDefault();
			if (selectedYear != null)
				selectedYear.Selected = true;

			return View("SmartStore.Plugin.Payments.PayPalDirect.Views.PaymentPayPalDirect.PaymentInfo", model);
		}

		[NonAction]
		public override IList<string> ValidatePaymentForm(FormCollection form) {
			var warnings = new List<string>();

			//validate
			var validator = new PaymentInfoValidator(_localizationService);
			var model = new PaymentInfoModel() {
				CardholderName = form["CardholderName"],
				CardNumber = form["CardNumber"],
				CardCode = form["CardCode"],
				ExpireMonth = form["ExpireMonth"],
				ExpireYear = form["ExpireYear"]
			};
			var validationResult = validator.Validate(model);
			if (!validationResult.IsValid)
				foreach (var error in validationResult.Errors)
					warnings.Add(error.ErrorMessage);
			return warnings;
		}

		[NonAction]
		public override ProcessPaymentRequest GetPaymentInfo(FormCollection form) {
			var paymentInfo = new ProcessPaymentRequest();
			paymentInfo.CreditCardType = form["CreditCardType"];
			paymentInfo.CreditCardName = form["CardholderName"];
			paymentInfo.CreditCardNumber = form["CardNumber"];
			paymentInfo.CreditCardExpireMonth = int.Parse(form["ExpireMonth"]);
			paymentInfo.CreditCardExpireYear = int.Parse(form["ExpireYear"]);
			paymentInfo.CreditCardCvv2 = form["CardCode"];
			return paymentInfo;
		}

		[ValidateInput(false)]
		public ActionResult IPNHandler() {
			Debug.WriteLine("PayPal Direct IPN: {0}".FormatWith(Request.ContentLength));

			byte[] param = Request.BinaryRead(Request.ContentLength);
			string strRequest = Encoding.ASCII.GetString(param);
			Dictionary<string, string> values;

			var processor = _paymentService.LoadPaymentMethodBySystemName("Payments.PayPalDirect") as PayPalDirectPaymentProcessor;
			if (processor == null || !processor.IsPaymentMethodActive(_paymentSettings) || !processor.PluginDescriptor.Installed)
				throw new SmartException(_helper.Resource("NoModuleLoading"));	// codehint: sm-edit

			if (processor.VerifyIPN(strRequest, out values)) {
				#region values
				decimal total = decimal.Zero;
				try {
					total = decimal.Parse(values["mc_gross"], new CultureInfo("en-US"));
				}
				catch { }

				string payer_status = string.Empty;
				values.TryGetValue("payer_status", out payer_status);
				string payment_status = string.Empty;
				values.TryGetValue("payment_status", out payment_status);
				string pending_reason = string.Empty;
				values.TryGetValue("pending_reason", out pending_reason);
				string mc_currency = string.Empty;
				values.TryGetValue("mc_currency", out mc_currency);
				string txn_id = string.Empty;
				values.TryGetValue("txn_id", out txn_id);
				string txn_type = string.Empty;
				values.TryGetValue("txn_type", out txn_type);
				string rp_invoice_id = string.Empty;
				values.TryGetValue("rp_invoice_id", out rp_invoice_id);
				string payment_type = string.Empty;
				values.TryGetValue("payment_type", out payment_type);
				string payer_id = string.Empty;
				values.TryGetValue("payer_id", out payer_id);
				string receiver_id = string.Empty;
				values.TryGetValue("receiver_id", out receiver_id);
				string invoice = string.Empty;
				values.TryGetValue("invoice", out invoice);
				string payment_fee = string.Empty;
				values.TryGetValue("payment_fee", out payment_fee);

				#endregion

				var sb = new StringBuilder();
				sb.AppendLine("PayPal IPN:");
				foreach (KeyValuePair<string, string> kvp in values) {
					sb.AppendLine(kvp.Key + ": " + kvp.Value);
				}

				var newPaymentStatus = PaypalHelper.GetPaymentStatus(payment_status, pending_reason);
				sb.AppendLine("{0}: {1}".FormatWith(_helper.Resource("NewPaymentStatus"), newPaymentStatus));		// codehint: sm-edit

				switch (txn_type) {
					case "recurring_payment_profile_created":
						//do nothing here
						break;
					case "recurring_payment":
						#region Recurring payment
						{
							Guid orderNumberGuid = Guid.Empty;
							try {
								orderNumberGuid = new Guid(rp_invoice_id);
							}
							catch {
							}

							var initialOrder = _orderService.GetOrderByGuid(orderNumberGuid);
							if (initialOrder != null) {
								var recurringPayments = _orderService.SearchRecurringPayments(0, 0, initialOrder.Id, null);
								foreach (var rp in recurringPayments) {
									switch (newPaymentStatus) {
										case PaymentStatus.Authorized:
										case PaymentStatus.Paid: {
												var recurringPaymentHistory = rp.RecurringPaymentHistory;
												if (recurringPaymentHistory.Count == 0) {
													//first payment
													var rph = new RecurringPaymentHistory() {
														RecurringPaymentId = rp.Id,
														OrderId = initialOrder.Id,
														CreatedOnUtc = DateTime.UtcNow
													};
													rp.RecurringPaymentHistory.Add(rph);
													_orderService.UpdateRecurringPayment(rp);
												}
												else {
													//next payments
													_orderProcessingService.ProcessNextRecurringPayment(rp);
												}
											}
											break;
									}
								}

								//this.OrderService.InsertOrderNote(newOrder.OrderId, sb.ToString(), DateTime.UtcNow);
								_logger.Information(_helper.Resource("IpnLogInfo"), new SmartException(sb.ToString()));		// codehint: sm-edit
							}
							else {
								_logger.Error(_helper.Resource("IpnOrderNotFound"), new SmartException(sb.ToString()));		// codehint: sm-edit
							}
						}
						#endregion
						break;
					default:
						 #region Standard payment
						 {
							 string orderNumber = string.Empty;
							 values.TryGetValue("custom", out orderNumber);
							 Guid orderNumberGuid = Guid.Empty;
							 try {
								 orderNumberGuid = new Guid(orderNumber);
							 }
							 catch {
							 }

							 var order = _orderService.GetOrderByGuid(orderNumberGuid);
							 if (order != null) {

								 //order note
								 order.OrderNotes.Add(new OrderNote() {
									 Note = sb.ToString(),
									 DisplayToCustomer = false,
									 CreatedOnUtc = DateTime.UtcNow
								 });
								 _orderService.UpdateOrder(order);

								 switch (newPaymentStatus) {
									 case PaymentStatus.Pending: {
										 }
										 break;
									 case PaymentStatus.Authorized: {
											 if (_orderProcessingService.CanMarkOrderAsAuthorized(order)) {
												 _orderProcessingService.MarkAsAuthorized(order);
											 }
										 }
										 break;
									 case PaymentStatus.Paid: {
											 if (_orderProcessingService.CanMarkOrderAsPaid(order)) {
												 _orderProcessingService.MarkOrderAsPaid(order);
											 }
										 }
										 break;
									 case PaymentStatus.Refunded: {
											 if (_orderProcessingService.CanRefundOffline(order)) {
												 _orderProcessingService.RefundOffline(order);
											 }
										 }
										 break;
									 case PaymentStatus.Voided: {
											 if (_orderProcessingService.CanVoidOffline(order)) {
												 _orderProcessingService.VoidOffline(order);
											 }
										 }
										 break;
									 default:
										 break;
								 }
							 }
							 else {
								 _logger.Error(_helper.Resource("IpnOrderNotFound"), new SmartException(sb.ToString()));
							 }
						 }
						 #endregion
						 break;
				}
			}
			else {
				_logger.Error(_helper.Resource("IpnFailed"), new SmartException(strRequest));		// codehint: sm-edit
			}

			//nothing should be rendered to visitor
			return Content("");
		}
	}
}